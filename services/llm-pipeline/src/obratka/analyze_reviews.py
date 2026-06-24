"""CLI for the production review-analysis JSON contract.

This module intentionally produces exactly two JSON artifacts:
`review_aspects.json` and `recommendations.json`.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import re
import sys
import threading
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from uuid import UUID

from obratka.config import Settings, get_settings
from obratka.llm.client import LLMClient
from obratka.llm.schemas import BusinessContext, RawReview, Recommendation, ReviewAnalysis
from obratka.logging_setup import get_logger, setup_logging
from obratka.observability.phoenix_setup import setup_phoenix
from obratka.runner import BatchRunner
from obratka.steps.step0_normalize import normalize_batch
from obratka.steps.step05_translate import step05_translate_all
from obratka.steps.step2_topics import BASE_TOPICS, step2_run
from obratka.steps.step21_cluster import apply_topic_map, cluster_topics
from obratka.steps.step22_reclassify import step22_reclassify_all
from obratka.steps.step3_kpi import aggregate_kpi
from obratka.steps.step4_recommend import generate_recommendations


SENTIMENTS = {"positive", "negative", "neutral", "mixed", "unknown"}
log = get_logger(__name__)

# Разовая инициализация логирования/трейсинга. `setup_logging` снимает все sink'и
# и пересоздаёт их — при параллельных jobs два конкурентных вызова могли бы
# потерять/задублировать sink'и, поэтому инициализируем строго один раз.
_runtime_init_lock = threading.Lock()
_runtime_initialized = False


def ensure_runtime_initialized(settings: Settings) -> None:
    """Разово настраивает логирование и Phoenix-трейсинг для процесса.

    Идемпотентна и потокобезопасна — повторные вызовы (в т.ч. из параллельных
    jobs) не пересоздают sink'и. Вызывается на старте воркера и в начале каждого
    `analyze_payload_llm`, чтобы CLI/QA-входы тоже инициализировались.
    """
    global _runtime_initialized
    if _runtime_initialized:
        return
    with _runtime_init_lock:
        if _runtime_initialized:
            return
        setup_logging(
            level=settings.log_level,
            logs_dir=settings.logs_dir,
            seq=settings.seq,
            environment=settings.app_env,
        )
        setup_phoenix(settings)
        _runtime_initialized = True

RU_TO_CONTRACT_SENTIMENT = {
    "очень позитивный": "positive",
    "позитивный": "positive",
    "очень негативный": "negative",
    "негативный": "negative",
    "нейтральный": "neutral",
    "смешанный": "mixed",
}


@dataclass(frozen=True)
class TopicRule:
    topic: str
    keywords: tuple[str, ...]


TOPIC_RULES: tuple[TopicRule, ...] = (
    TopicRule("качество лечения", ("качественно", "безболезн", "помогли", "результат", "зуб спас", "лечение")),
    TopicRule("врач", ("врач", "доктор", "специалист", "ортопед", "хирург", "терапевт")),
    TopicRule("администратор", ("администратор", "ресепш", "рецепш", "запись", "whatsapp", "ватсап")),
    TopicRule("сервис", ("сервис", "отношение", "забота", "встретили", "чай", "кофе", "персонал")),
    TopicRule("цена", ("цен", "дорог", "недорог", "стоимост", "акци", "прайс")),
    TopicRule("ожидание", ("ждал", "ждала", "ждать", "очеред", "вовремя", "по записи")),
    TopicRule("интерьер", ("интерьер", "уют", "красиво", "современно", "обстанов")),
    TopicRule("чистота", ("пыль", "чист", "гряз", "стериль", "уборк")),
    TopicRule("оборудование", ("микроскоп", "кт", "3d", "сканер", "аксиограф", "оборудован")),
    TopicRule("диагностика", ("сним", "кт", "диагност", "план лечения", "обследован")),
    TopicRule("коммуникация", ("объясн", "ответ", "информац", "рассказ", "не сказ", "игнор")),
    TopicRule("детский приём", ("ребён", "ребен", "дочь", "сын", "детск", "испуг")),
    TopicRule("ортодонтия", ("брекет", "элайнер", "прикус", "пластин", "ортодонт")),
    TopicRule("имплантация", ("имплант", "имплантац", "костная пластика")),
    TopicRule("протезирование", ("корон", "винир", "протез", "all-on-4", "all-on-6")),
    TopicRule("ВНЧС / гнатология", ("сустав", "щелч", "челюст", "капа", "сплинт", "гнатолог")),
)

POSITIVE_WORDS = (
    "спасибо", "благодар", "качествен", "ответствен", "хорош", "отличн",
    "прекрасн", "вниматель", "профессион", "довол", "рекоменд", "помог",
    "уют", "чист", "современ", "безболезн", "спокойн",
)
NEGATIVE_WORDS = (
    "плохо", "ужас", "негатив", "не понрав", "дорог", "ждал", "очеред",
    "пыль", "гряз", "игнор", "не ответ", "не объясн", "больно", "хам",
    "груб", "навяз", "проблем", "отказ", "не хотел", "много пыли",
)
HIGH_RISK_TOPICS = {"качество лечения", "чистота", "коммуникация", "администратор", "ожидание", "врач"}


class InputError(ValueError):
    pass


def _load_input(path: Path) -> dict[str, Any]:
    try:
        with path.open("r", encoding="utf-8") as f:
            data = json.load(f)
    except json.JSONDecodeError as e:
        raise InputError(f"Некорректный JSON: {e.msg} at line {e.lineno}, column {e.colno}") from e
    except OSError as e:
        raise InputError(f"Не удалось прочитать входной файл: {e}") from e
    if not isinstance(data, dict):
        raise InputError("Входной JSON должен быть объектом верхнего уровня.")
    return data


def _validate_input(data: dict[str, Any]) -> None:
    required_top = ("schema_version", "analysis_job_id", "company_id", "reviews")
    for field in required_top:
        if field not in data:
            raise InputError(f"Отсутствует обязательное поле верхнего уровня: {field}")
    if str(data["schema_version"]) not in {"1.0", "2.0"}:
        raise InputError("Неподдерживаемая schema_version: ожидается '1.0' или '2.0'.")
    if not isinstance(data["reviews"], list):
        raise InputError("Поле reviews должно быть массивом.")

    required_review = ("review_id", "text", "source", "date", "stars", "branch_id")
    for idx, review in enumerate(data["reviews"]):
        if not isinstance(review, dict):
            raise InputError(f"reviews[{idx}] должен быть объектом.")
        for field in required_review:
            if field not in review:
                raise InputError(f"reviews[{idx}] отсутствует обязательное поле: {field}")
        if not isinstance(review["text"], str):
            raise InputError(f"reviews[{idx}].text должен быть строкой.")
        try:
            datetime.fromisoformat(str(review["date"]).replace("Z", "+00:00"))
        except ValueError as e:
            raise InputError(f"reviews[{idx}].date должен быть ISO-8601 датой.") from e


def _stars_value(stars: Any) -> float | None:
    try:
        return float(stars)
    except (TypeError, ValueError):
        return None


def _sentiment_from_counts(pos: int, neg: int, stars: float | None, empty: bool) -> tuple[str, float]:
    if empty:
        return "unknown", 0.0
    if pos > 0 and neg > 0:
        if abs(pos - neg) <= max(pos, neg):
            return "mixed", 0.82
    if neg > pos:
        return "negative", min(0.95, 0.65 + neg * 0.08)
    if pos > neg:
        return "positive", min(0.95, 0.65 + pos * 0.08)
    if stars is not None:
        if stars >= 4:
            return "positive", 0.65
        if stars <= 2:
            return "negative", 0.65
    return "neutral", 0.55


def _sentence_fragment(text: str, keyword: str) -> str:
    parts = re.split(r"(?<=[.!?…])\s+|\n+", text)
    low_keyword = keyword.lower()
    for part in parts:
        if low_keyword in part.lower():
            return part.strip()[:240]
    idx = text.lower().find(low_keyword)
    if idx < 0:
        return ""
    start = max(0, idx - 60)
    end = min(len(text), idx + len(keyword) + 120)
    return text[start:end].strip()


def _aspect_sentiment(fragment: str, stars: float | None) -> tuple[str, float]:
    low = fragment.lower()
    pos = sum(1 for w in POSITIVE_WORDS if w in low)
    neg = sum(1 for w in NEGATIVE_WORDS if w in low)
    sentiment, conf = _sentiment_from_counts(pos, neg, stars, empty=not fragment.strip())
    if sentiment == "mixed":
        conf = 0.78
    return sentiment, conf


def _analyze_one(review: dict[str, Any]) -> dict[str, Any]:
    review_id = str(review["review_id"])
    text = review["text"]
    stars = _stars_value(review.get("stars"))
    low = text.lower()
    empty = not text.strip()

    if empty:
        return {
            "review_id": review_id,
            "text": text,
            "overall_sentiment": "unknown",
            "overall_confidence": 0,
            "aspects": [],
        }

    aspects = []
    seen_topics: set[str] = set()
    pos_signals = sum(1 for w in POSITIVE_WORDS if w in low)
    neg_signals = sum(1 for w in NEGATIVE_WORDS if w in low)

    for rule in TOPIC_RULES:
        matched = next((kw for kw in rule.keywords if kw in low), None)
        if matched is None:
            continue
        fragment = _sentence_fragment(text, matched)
        if fragment and fragment not in text:
            fragment = ""
        sentiment, confidence = _aspect_sentiment(fragment, stars)
        aspects.append(
            {
                "topic": rule.topic,
                "sentiment": sentiment,
                "confidence": float(max(0.0, min(1.0, confidence))),
                "fragment": fragment,
                "is_freeform": False,
            }
        )
        seen_topics.add(rule.topic)

    if not aspects and len(text.strip()) >= 5:
        sentiment, confidence = _sentiment_from_counts(pos_signals, neg_signals, stars, empty=False)
        aspects.append(
            {
                "topic": "общее впечатление",
                "sentiment": sentiment,
                "confidence": float(confidence),
                "fragment": text.strip()[:240],
                "is_freeform": True,
            }
        )

    overall, overall_conf = _sentiment_from_counts(pos_signals, neg_signals, stars, empty=False)
    if any(a["sentiment"] == "negative" for a in aspects) and any(
        a["sentiment"] == "positive" for a in aspects
    ):
        overall, overall_conf = "mixed", 0.86

    return {
        "review_id": review_id,
        "text": text,
        "overall_sentiment": overall,
        "overall_confidence": float(max(0.0, min(1.0, overall_conf))),
        "aspects": aspects,
    }


def _parse_date(value: str) -> datetime:
    dt = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    if dt.tzinfo is None:
        return dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def _recommendations(aspects: list[dict[str, Any]], reviews: list[dict[str, Any]]) -> dict[str, Any]:
    if not reviews:
        return {
            "recommendations_count": 0,
            "summary": "Недостаточно данных для формирования рекомендаций: входной файл не содержит отзывов.",
            "full_recommendations": [],
        }

    review_dates = {str(r["review_id"]): _parse_date(r["date"]) for r in reviews}
    topic_stats: dict[str, dict[str, Any]] = defaultdict(
        lambda: {
            "mentions": 0,
            "negative": 0,
            "positive": 0,
            "sources": set(),
            "evidence": [],
            "latest": None,
        }
    )
    review_by_id = {str(r["review_id"]): r for r in reviews}

    for item in aspects:
        rid = item["review_id"]
        source = review_by_id.get(rid, {}).get("source", "unknown")
        for aspect in item["aspects"]:
            topic = aspect["topic"]
            stats = topic_stats[topic]
            stats["mentions"] += 1
            stats["sources"].add(source)
            if aspect["sentiment"] == "negative":
                stats["negative"] += 1
            if aspect["sentiment"] == "positive":
                stats["positive"] += 1
            dt = review_dates.get(rid)
            if dt and (stats["latest"] is None or dt > stats["latest"]):
                stats["latest"] = dt
            fragment = aspect.get("fragment") or f"review_id={rid}"
            if fragment and len(stats["evidence"]) < 4:
                stats["evidence"].append(f"{fragment} (review_id={rid})")

    recs = []
    for topic, stats in topic_stats.items():
        mentions = stats["mentions"]
        negative = stats["negative"]
        if mentions < 3 and topic not in HIGH_RISK_TOPICS:
            continue
        if negative < 2 and topic not in HIGH_RISK_TOPICS:
            continue
        if negative == 0:
            continue
        sources_count = len(stats["sources"])
        priority = 1 if topic in HIGH_RISK_TOPICS and negative >= 2 else 2
        if sources_count >= 2:
            priority = min(priority, 2)
        title = f"Разобрать повторяющиеся жалобы: {topic}"
        body = (
            f"Проверить процесс по теме «{topic}», разобрать свежие негативные "
            "отзывы с ответственными сотрудниками и закрепить понятный стандарт реакции."
        )
        expected = "Меньше повторяющихся жалоб и выше предсказуемость клиентского опыта."
        recs.append(
            {
                "priority": priority,
                "topic": topic,
                "title": title,
                "body": body,
                "expected_impact": expected,
                "evidence": list(stats["evidence"])[:4],
            }
        )

    positive_topics = sorted(
        (
            (topic, stats)
            for topic, stats in topic_stats.items()
            if stats["positive"] >= 3 and stats["positive"] >= stats["negative"]
        ),
        key=lambda kv: kv[1]["positive"],
        reverse=True,
    )
    if positive_topics:
        top_topic, stats = positive_topics[0]
        recs.append(
            {
                "priority": 3,
                "topic": top_topic,
                "title": f"Использовать сильную сторону: {top_topic}",
                "body": (
                    f"Подсветить тему «{top_topic}» в коммуникациях и сохранить "
                    "стандарт, который клиенты уже отмечают положительно."
                ),
                "expected_impact": "Больше доверия к сильным направлениям и понятнее выбор для новых клиентов.",
                "evidence": list(stats["evidence"])[:4],
            }
        )

    recs.sort(key=lambda r: (r["priority"], -len(r["evidence"])))
    summary = (
        "Рекомендации сформированы на основе повторяющихся тем в отзывах."
        if recs
        else "Недостаточно повторяющихся сигналов для формирования рекомендаций."
    )
    return {
        "recommendations_count": len(recs),
        "summary": summary,
        "full_recommendations": recs,
    }


def analyze_payload(data: dict[str, Any]) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    """Fast local contract check without LLM.

    The production path is `analyze_payload_llm`; this function is kept as a
    deterministic fallback for smoke tests and contract validation.
    """
    _validate_input(data)
    aspects = [_analyze_one(review) for review in data["reviews"]]
    recommendations = _recommendations(aspects, data["reviews"])
    return aspects, recommendations


def _business_id_from_company_id(company_id: Any) -> int:
    try:
        value = int(UUID(str(company_id)))
    except (TypeError, ValueError):
        return 1
    return value % 2_147_483_647 or 1


def _raw_reviews_from_contract(reviews: list[dict[str, Any]]) -> list[RawReview]:
    raw: list[RawReview] = []
    for review in reviews:
        stars = _stars_value(review.get("stars"))
        raw.append(
            RawReview(
                review_id=str(review["review_id"]),
                author_id=None,
                text=review["text"],
                stars=int(round(stars)) if stars is not None else None,
                date=_parse_date(str(review["date"])),
                source=str(review["source"]),
            )
        )
    return raw


def _contract_sentiment(value: str | None) -> str:
    if value is None:
        return "unknown"
    out = RU_TO_CONTRACT_SENTIMENT.get(value, value)
    return out if out in SENTIMENTS else "unknown"


def _confidence(value: Any) -> float:
    try:
        return float(max(0.0, min(1.0, float(value))))
    except (TypeError, ValueError):
        return 0.0


def _source_fragment(original: str, fragment: str | None) -> str:
    if not fragment:
        return ""
    if fragment in original:
        return fragment
    original_low = original.casefold()
    fragment_low = fragment.casefold()
    idx = original_low.find(fragment_low)
    if idx >= 0:
        return original[idx : idx + len(fragment)]

    compact_original = re.sub(r"\s+", " ", original)
    compact_fragment = re.sub(r"\s+", " ", fragment).strip()
    if compact_fragment and compact_fragment in compact_original:
        return compact_fragment
    return ""


def _empty_contract_item(review: dict[str, Any]) -> dict[str, Any]:
    return {
        "review_id": str(review["review_id"]),
        "text": review["text"],
        "overall_sentiment": "unknown",
        "overall_confidence": 0,
        "aspects": [],
    }


def _analysis_to_contract(
    review: dict[str, Any],
    analysis: ReviewAnalysis | None,
) -> dict[str, Any]:
    if not review["text"].strip() or analysis is None:
        return _empty_contract_item(review)

    text = review["text"]
    aspects = []
    for aspect in analysis.aspects:
        fragment = _source_fragment(text, aspect.fragment)
        aspects.append(
            {
                "topic": aspect.topic,
                "sentiment": _contract_sentiment(aspect.sentiment),
                "confidence": _confidence(aspect.confidence),
                "fragment": fragment,
                "is_freeform": bool(aspect.is_freeform),
            }
        )

    return {
        "review_id": str(review["review_id"]),
        "text": text,
        "overall_sentiment": _contract_sentiment(analysis.overall_sentiment),
        "overall_confidence": _confidence(analysis.overall_confidence),
        "aspects": aspects,
    }


def _recommendations_to_contract(
    recommendations: list[Recommendation],
    summary: str,
) -> dict[str, Any]:
    out = [
        {
            "priority": int(rec.priority),
            "topic": rec.topic or "",
            "title": rec.title,
            "body": rec.body,
            "expected_impact": rec.expected_impact.replace("%", ""),
            "evidence": list(rec.evidence),
        }
        for rec in recommendations
    ]
    out.sort(key=lambda r: (r["priority"], -len(r["evidence"])))
    return {
        "recommendations_count": len(out),
        "summary": summary,
        "full_recommendations": out,
    }


async def analyze_payload_llm(
    data: dict[str, Any],
    *,
    settings: Settings | None = None,
    llm: LLMClient | None = None,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    """Run the real multi-step LLM pipeline and adapt it to the JSON contract."""
    _validate_input(data)
    reviews = data["reviews"]
    if not reviews:
        return [], _recommendations([], [])

    s = settings or get_settings()
    ensure_runtime_initialized(s)

    llm_client = llm or LLMClient(api_key=s.openrouter_api_key, models=s.models)
    runner: BatchRunner = BatchRunner(max_concurrency=s.pipeline.max_concurrency)
    business_id = _business_id_from_company_id(data["company_id"])
    raw = _raw_reviews_from_contract(reviews)

    log.info("Contract LLM pipeline start", reviews_count=len(raw))
    normalized = normalize_batch(raw)
    translations = await step05_translate_all(normalized, runner, llm_client)
    high_conf, low_conf_queue = await step2_run(
        normalized,
        translations,
        runner,
        llm_client,
        batch_size=s.pipeline.step2_batch_size,
        low_conf_threshold=s.pipeline.step2_low_conf_threshold,
    )

    all_topics = set()
    for analysis in high_conf:
        for aspect in analysis.aspects:
            if aspect.is_freeform or aspect.topic not in BASE_TOPICS:
                all_topics.add(aspect.topic)
    for item in low_conf_queue:
        for aspect in item.initial_analysis.aspects:
            if aspect.is_freeform or aspect.topic not in BASE_TOPICS:
                all_topics.add(aspect.topic)

    topic_map = await cluster_topics(list(all_topics), BASE_TOPICS, llm_client)
    available_topics = BASE_TOPICS + topic_map.canonical_topics
    reclassified = await step22_reclassify_all(low_conf_queue, available_topics, llm_client)
    final_analyses = apply_topic_map(high_conf + reclassified, topic_map)

    analysis_by_id = {analysis.review_id: analysis for analysis in final_analyses}
    review_aspects = [
        _analysis_to_contract(review, analysis_by_id.get(str(review["review_id"])))
        for review in reviews
    ]

    pipeline_result = aggregate_kpi(
        final_analyses,
        normalized,
        business_id,
        weighting=s.pipeline.weighting,
    )
    context = BusinessContext(
        business_id=business_id,
        name=None,
        business_type="unknown",
        location=None,
    )
    recs = await generate_recommendations(pipeline_result, context, llm_client)
    recommendations = _recommendations_to_contract(recs.recommendations, recs.summary)
    log.info(
        "Contract LLM pipeline done",
        recommendations_count=recommendations["recommendations_count"],
        total_cost_usd=getattr(llm_client, "total_cost", 0.0),
    )
    return review_aspects, recommendations


def write_outputs(aspects: list[dict[str, Any]], recommendations: dict[str, Any], out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "review_aspects.json").write_text(
        json.dumps(aspects, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    (out_dir / "recommendations.json").write_text(
        json.dumps(recommendations, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m obratka.analyze_reviews")
    parser.add_argument("--input", required=True, help="Path to input reviews JSON.")
    parser.add_argument("--out-dir", required=True, help="Directory for output JSON files.")
    parser.add_argument(
        "--local",
        action="store_true",
        help="Use deterministic local fallback instead of the LLM pipeline.",
    )
    args = parser.parse_args(argv)

    try:
        data = _load_input(Path(args.input))
        if args.local:
            aspects, recommendations = analyze_payload(data)
        else:
            aspects, recommendations = asyncio.run(analyze_payload_llm(data))
        write_outputs(aspects, recommendations, Path(args.out_dir))
    except InputError as e:
        print(f"Input error: {e}", file=sys.stderr)
        return 2
    except Exception as e:
        print(f"Analysis error: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
