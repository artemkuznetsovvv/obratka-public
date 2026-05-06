"""Адаптер между core-пайплайном и контрактом PG.

Ядро (`obratka.analyze_reviews.analyze_payload_llm`) возвращает кортеж
`(aspects_list, recommendations_dict)`:
- английские sentiment-ы (`positive`/`negative`/`neutral`/`mixed`/`unknown`);
- `review_id` приведён к строке;
- recommendations: `{recommendations_count, summary, full_recommendations[]}`.

Выход web-обёртки — два файла:
- `output_reviews.json` — формат QUICKSTART 2.0 (русские sentiment-ы,
  `review_id` исходного типа из input).
- `output_summary.json` — формат `tasks/codex_reviews_analysis_requirements.md`
  пункт 2 (`recommendations_count`/`summary`/`full_recommendations`).
  `schema_version` + `analysis_job_id` сверху — метаданные для трассировки.
"""

from __future__ import annotations

from typing import Any


SCHEMA_VERSION = "2.0"

# 5 значений ядра → 3 значения контракта 2.0. mixed/unknown маппим в "нейтральный",
# чтобы не нарушить замкнутый список из QUICKSTART (позитивный/негативный/нейтральный).
EN_TO_RU_SENTIMENT: dict[str, str] = {
    "positive": "позитивный",
    "negative": "негативный",
    "neutral":  "нейтральный",
    "mixed":    "нейтральный",
    "unknown":  "нейтральный",
}


def _to_ru(value: str | None) -> str:
    if not value:
        return "нейтральный"
    return EN_TO_RU_SENTIMENT.get(value, "нейтральный")


def _coerce_confidence(value: Any) -> float:
    try:
        v = float(value)
    except (TypeError, ValueError):
        return 0.0
    return max(0.0, min(1.0, v))


def _build_review_item(
    *,
    original_review_id: int | str,
    original_text: str,
    core_item: dict[str, Any] | None,
) -> dict[str, Any]:
    if core_item is None:
        return {
            "review_id":          original_review_id,
            "text":               original_text,
            "overall_sentiment":  "нейтральный",
            "overall_confidence": 0.0,
            "aspects":            [],
        }

    aspects_out: list[dict[str, Any]] = []
    for asp in core_item.get("aspects", []) or []:
        aspects_out.append(
            {
                "topic":       asp.get("topic", "") or "",
                "sentiment":   _to_ru(asp.get("sentiment")),
                "confidence":  _coerce_confidence(asp.get("confidence")),
                "fragment":    asp.get("fragment") or "",
                "is_freeform": bool(asp.get("is_freeform", False)),
            }
        )

    return {
        "review_id":          original_review_id,
        "text":               core_item.get("text", original_text),
        "overall_sentiment":  _to_ru(core_item.get("overall_sentiment")),
        "overall_confidence": _coerce_confidence(core_item.get("overall_confidence")),
        "aspects":            aspects_out,
    }


def _normalize_full_recommendations(items: list[Any] | None) -> list[dict[str, Any]]:
    """Защитное приведение элементов `full_recommendations` к codex-схеме.

    Ядро уже возвращает корректную форму (см. `_recommendations_to_contract`),
    но входной dict может прийти из других источников/тестов с пропусками —
    подставляем дефолты, чтобы выход всегда был валидным.
    """
    out: list[dict[str, Any]] = []
    for r in items or []:
        if not isinstance(r, dict):
            continue
        out.append(
            {
                "priority":        int(r.get("priority", 3)),
                "topic":           str(r.get("topic") or ""),
                "title":           str(r.get("title") or ""),
                "body":            str(r.get("body") or ""),
                "expected_impact": str(r.get("expected_impact") or ""),
                "evidence":        [str(x) for x in (r.get("evidence") or [])],
            }
        )
    # По codex: сортируем по priority (asc), при равенстве — по числу evidence (desc).
    out.sort(key=lambda r: (r["priority"], -len(r["evidence"])))
    return out


def build_outputs(
    *,
    analysis_job_id: str,
    input_reviews: list[dict[str, Any]],
    core_aspects: list[dict[str, Any]],
    core_recommendations: dict[str, Any],
) -> tuple[dict[str, Any], dict[str, Any]]:
    """Финальные `output_reviews.json` + `output_summary.json`.

    `output_reviews.json` — формат QUICKSTART 2.0 (русские sentiment-ы,
        `review_id` сохраняется в исходном типе из input — PG матчит по int64).
    `output_summary.json` — формат `codex_reviews_analysis_requirements.md` §2
        (`recommendations_count`/`summary`/`full_recommendations`).
        `schema_version` + `analysis_job_id` — метаданные сверху.

    Ядро возвращает `review_id` как строку, поэтому матчим по `str(original_id)`,
    но в выход возвращаем оригинальный тип (int → int).
    """
    aspect_by_id = {item["review_id"]: item for item in core_aspects}

    reviews_out: list[dict[str, Any]] = []
    for raw in input_reviews:
        original_id = raw["review_id"]
        core_item = aspect_by_id.get(str(original_id))
        reviews_out.append(
            _build_review_item(
                original_review_id=original_id,
                original_text=raw.get("text", ""),
                core_item=core_item,
            )
        )

    full_recommendations = _normalize_full_recommendations(
        core_recommendations.get("full_recommendations")
    )

    output_reviews = {
        "schema_version":  SCHEMA_VERSION,
        "analysis_job_id": analysis_job_id,
        "reviews":         reviews_out,
    }
    output_summary = {
        "schema_version":        SCHEMA_VERSION,
        "analysis_job_id":       analysis_job_id,
        "recommendations_count": len(full_recommendations),
        "summary":               str(core_recommendations.get("summary") or ""),
        "full_recommendations":  full_recommendations,
    }
    return output_reviews, output_summary
