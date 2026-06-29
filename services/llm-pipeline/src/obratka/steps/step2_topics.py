from __future__ import annotations

import random
import time
from datetime import datetime
from typing import TYPE_CHECKING

from obratka.llm.client import LLMClient
from obratka.llm.schemas import (
    BatchOutput,
    LowConfItem,
    NormalizedReview,
    ReviewAnalysis,
    TranslatedReview,
)
from obratka.logging_setup import get_logger
from obratka.observability.spans import batch_span, step_span
from obratka.runner import BatchRunner
from obratka.utils.topics import BASE_TOPICS, normalize_analysis_topics

if TYPE_CHECKING:
    from obratka.report.artifacts import ArtifactCollector

log = get_logger(__name__)


def needs_reclassification(analysis: ReviewAnalysis, threshold: float) -> tuple[bool, str]:
    if analysis.overall_confidence < threshold:
        return True, "low_overall_confidence"
    if any(a.confidence < threshold for a in analysis.aspects):
        return True, "low_aspect_confidence"
    return False, ""


async def step2_process_batch(
    batch: list[NormalizedReview],
    translations: dict[str, TranslatedReview],
    llm: LLMClient,
    batch_id: str = "0000",
) -> tuple[list[ReviewAnalysis], int]:
    """Возвращает (analyses, latency_ms)."""
    sys_prompt = (
        "Ты — система анализа отзывов о бизнесах.\n"
        "Для каждого отзыва из батча определи:\n"
        "1. Все упомянутые темы (аспекты). Используй базовый набор тем где возможно;\n"
        "   если нужна тема вне списка — добавь её и пометь is_freeform=true.\n"
        "2. Тональность каждого аспекта.\n"
        "3. Общую тональность отзыва (overall_sentiment).\n"
        "4. is_mixed=true, если в отзыве есть и явный позитив, и явный негатив.\n"
        "5. confidence для каждого аспекта и для overall_sentiment — насколько ты уверена.\n"
        "   Если формулировка размыта, ирония, сарказм или мало контекста — confidence ниже.\n\n"
        "Базовый набор тем:\n"
        + "\n".join(f"- {t}" for t in BASE_TOPICS)
        + "\n\n"
        "Для каждого аспекта приведи fragment — точную цитату из отзыва.\n"
        'Верни строго JSON по схеме {"items": [...]}.\n'
    )

    batch_text = []
    for i, r in enumerate(batch):
        text = translations.get(
            r.review_id,
            TranslatedReview(review_id="", text_translated=r.text_normalized, source_lang="ru"),
        ).text_translated
        batch_text.append(f"[{i + 1}] ID: {r.review_id}\nText: {text}")

    prompt = "\n\n".join(batch_text)

    started = time.perf_counter()
    try:
        async with batch_span("step2", batch_id, len(batch)):
            content, _ = await llm.complete(
                model="topics",
                response_model=BatchOutput,
                messages=[
                    {"role": "system", "content": sys_prompt},
                    {"role": "user", "content": prompt},
                ],
            )
        latency_ms = int((time.perf_counter() - started) * 1000)
        return content.items, latency_ms
    except Exception as e:
        log.error("Batch topics parsing failed", error=str(e), batch_id=batch_id)
        latency_ms = int((time.perf_counter() - started) * 1000)
        return [], latency_ms


async def step2_run(
    reviews: list[NormalizedReview],
    translations: dict[str, TranslatedReview],
    runner: BatchRunner,
    llm: LLMClient,
    batch_size: int = 12,
    low_conf_threshold: float = 0.5,
    *,
    collector: "ArtifactCollector | None" = None,
) -> tuple[list[ReviewAnalysis], list[LowConfItem]]:
    started_at = datetime.now()
    async with step_span("step2", reviews_count=len(reviews), batch_size=batch_size) as span:
        log.info("Step 2 start", reviews_count=len(reviews), batch_size=batch_size)

        batches = [reviews[i : i + batch_size] for i in range(0, len(reviews), batch_size)]
        indexed_batches = [(f"batch_{i:04d}", b) for i, b in enumerate(batches)]

        cost_before = llm.total_cost if llm is not None else 0.0

        batch_results: list[tuple[list[ReviewAnalysis], int]] = await runner.run_many(
            indexed_batches,
            worker=lambda ib: step2_process_batch(ib[1], translations, llm, batch_id=ib[0]),
        )

        cost_after = llm.total_cost if llm is not None else 0.0

        high_conf: list[ReviewAnalysis] = []
        low_conf_queue: list[LowConfItem] = []
        parse_failures = 0
        latencies: list[int] = []

        review_text_map = {
            r.review_id: translations.get(
                r.review_id,
                TranslatedReview(review_id="", text_translated=r.text_normalized, source_lang=""),
            ).text_translated
            for r in reviews
        }

        for analyses, latency_ms in batch_results:
            latencies.append(latency_ms)
            if not analyses:
                parse_failures += 1
                continue
            analyses = normalize_analysis_topics(analyses)
            for a in analyses:
                needs_reclass, reason = needs_reclassification(a, low_conf_threshold)
                if needs_reclass:
                    low_conf_queue.append(
                        LowConfItem(
                            review_id=a.review_id,
                            text=review_text_map.get(a.review_id, ""),
                            initial_analysis=a,
                            reason=reason,
                        )
                    )
                else:
                    high_conf.append(a)

        low_conf_pct = round(100 * len(low_conf_queue) / max(1, len(reviews)), 1)
        log.info(
            "Step 2 done",
            high_conf=len(high_conf),
            low_conf=len(low_conf_queue),
            low_conf_pct=low_conf_pct,
        )
        span.set_attribute("batches_count", len(batches))
        span.set_attribute("high_conf_count", len(high_conf))
        span.set_attribute("low_conf_count", len(low_conf_queue))
        span.set_attribute("cost_usd", cost_after - cost_before)

        if collector is not None:
            from obratka.report.artifacts import (
                ReviewAnalysisSample,
                Step2Artifact,
                make_stage_stats,
            )

            all_analyses = list(high_conf) + [q.initial_analysis for q in low_conf_queue]
            # Confidence histogram (10 buckets 0..1)
            hist = [0] * 10
            for a in all_analyses:
                idx = min(9, max(0, int(a.overall_confidence * 10)))
                hist[idx] += 1

            # Sentiment distribution
            sentiment_dist: dict[str, int] = {}
            for a in all_analyses:
                sentiment_dist[a.overall_sentiment] = sentiment_dist.get(a.overall_sentiment, 0) + 1

            # Topic distribution (top-20)
            topic_counts: dict[str, int] = {}
            freeform_count = 0
            for a in all_analyses:
                for asp in a.aspects:
                    topic_counts[asp.topic] = topic_counts.get(asp.topic, 0) + 1
                    if asp.is_freeform or asp.topic not in BASE_TOPICS:
                        freeform_count += 1
            top_topics = dict(
                sorted(topic_counts.items(), key=lambda kv: kv[1], reverse=True)[:20]
            )

            def _to_sample(a: ReviewAnalysis) -> ReviewAnalysisSample:
                return ReviewAnalysisSample(
                    review_id=a.review_id,
                    text=review_text_map.get(a.review_id, "")[:500],
                    overall_sentiment=a.overall_sentiment,
                    overall_confidence=a.overall_confidence,
                    aspects=[asp.model_dump() for asp in a.aspects],
                )

            hi_n = min(collector.max_samples, 5, len(high_conf))
            lo_n = min(max(collector.max_samples, 10), 50, len(low_conf_queue))
            hi_chosen = random.sample(high_conf, hi_n) if hi_n > 0 else []
            lo_chosen = random.sample(
                [q.initial_analysis for q in low_conf_queue], lo_n
            ) if lo_n > 0 else []

            avg_latency = int(sum(latencies) / len(latencies)) if latencies else 0
            collector.record_step2(
                Step2Artifact(
                    stats=make_stage_stats(
                        "step2", started_at, cost_usd=cost_after - cost_before
                    ),
                    batches_count=len(batches),
                    avg_batch_latency_ms=avg_latency,
                    high_conf_count=len(high_conf),
                    low_conf_count=len(low_conf_queue),
                    low_conf_pct=low_conf_pct,
                    confidence_histogram=hist,
                    sentiment_distribution=sentiment_dist,
                    topics_distribution=top_topics,
                    freeform_topics_count=freeform_count,
                    parse_failures=parse_failures,
                    samples_high_conf=[_to_sample(a) for a in hi_chosen],
                    samples_low_conf=[_to_sample(a) for a in lo_chosen],
                )
            )

        return high_conf, low_conf_queue
