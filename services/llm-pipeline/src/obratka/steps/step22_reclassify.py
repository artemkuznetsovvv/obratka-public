import random
from datetime import datetime
from typing import TYPE_CHECKING

from obratka.llm.client import LLMClient
from obratka.llm.schemas import ReclassifyOutput, LowConfItem, ReviewAnalysis, Aspect
from obratka.runner import BatchRunner
from obratka.logging_setup import get_logger
from obratka.observability.spans import step_span
from obratka.utils.topics import normalize_analysis_topics

if TYPE_CHECKING:
    from obratka.report.artifacts import ArtifactCollector, ReviewAnalysisSample

log = get_logger(__name__)

async def reclassify_one(
    item: LowConfItem,
    available_topics: list[str],
    llm: LLMClient,
) -> ReclassifyOutput:
    sys_prompt = (
        "Ты — точный аналитик отзывов. Предыдущая модель не была уверена в анализе этого отзыва.\n"
        "Твоя задача — дать максимально точный анализ тем и тональности.\n\n"
        "Используй темы только из списка available_topics, если они подходят.\n"
        "Если ни одна не подходит — добавь свободную с is_freeform=true.\n\n"
        "Особое внимание:\n"
        "- Ирония и сарказм\n"
        "- Двусмысленные формулировки\n"
        "- Смешанные отзывы с равными долями позитива и негатива\n"
        "- Очень короткие отзывы — оценивай conservatively\n\n"
        "Если даже после анализа уверенность < 0.5 — поставь low_confidence_final=true,\n"
        "но всё равно выбери наиболее вероятный вариант.\n\n"
        "Верни строго JSON по схеме."
    )

    initial_aspects = [a.topic for a in item.initial_analysis.aspects]
    prompt = (
        f"Review ID: {item.review_id}\n"
        f"Text: {item.text}\n"
        f"Initial topics from weak model: {initial_aspects}\n"
        f"Available Topics: {available_topics}\n"
    )

    try:
        content, _ = await llm.complete(
            model="topics_strong",
            response_model=ReclassifyOutput,
            messages=[
                {"role": "system", "content": sys_prompt},
                {"role": "user", "content": prompt}
            ],
            request_id=item.review_id
        )
        return content
    except Exception as e:
        log.error("Reclassify failed", review_id=item.review_id, error=str(e))
        return ReclassifyOutput(
            review_id=item.review_id,
            is_mixed=item.initial_analysis.is_mixed,
            overall_sentiment=item.initial_analysis.overall_sentiment,
            overall_confidence=item.initial_analysis.overall_confidence,
            aspects=item.initial_analysis.aspects,
            low_confidence_final=True
        )

async def step22_reclassify_all(
    queue: list[LowConfItem],
    available_topics: list[str],
    llm: LLMClient,
    *,
    collector: "ArtifactCollector | None" = None,
) -> list[ReviewAnalysis]:
    started_at = datetime.now()
    async with step_span("step22", queue_size=len(queue)) as span:
        if not queue:
            log.info("No low-confidence reviews, skipping")
            span.set_attribute("reclassified_count", 0)
            if collector is not None:
                from obratka.report.artifacts import Step22Artifact, make_stage_stats

                collector.record_step22(
                    Step22Artifact(
                        stats=make_stage_stats("step22", started_at),
                        queue_size=0,
                        reclassified_count=0,
                        still_low_conf_count=0,
                        samples=[],
                    )
                )
            return []

        log.info("Step 2.2 start", queue_size=len(queue))

        strong_runner = BatchRunner(max_concurrency=4)
        cost_before = llm.total_cost if llm is not None else 0.0

        results: list[ReclassifyOutput] = await strong_runner.run_many(
            queue,
            worker=lambda item: reclassify_one(item, available_topics, llm),
        )
        cost_after = llm.total_cost if llm is not None else 0.0

        final = [
            ReviewAnalysis(
                review_id=r.review_id,
                is_mixed=r.is_mixed,
                overall_sentiment=r.overall_sentiment,
                overall_confidence=r.overall_confidence,
                aspects=r.aspects,
                low_confidence_final=r.low_confidence_final
            )
            for r in results
        ]
        final = normalize_analysis_topics(final)

        still_low = sum(1 for r in results if r.low_confidence_final)
        log.info(
            "Step 2.2 done",
            reclassified=len(results),
            still_low_confidence=still_low,
        )
        span.set_attribute("reclassified_count", len(results))
        span.set_attribute("still_low_conf_count", still_low)
        span.set_attribute("cost_usd", cost_after - cost_before)

        if collector is not None:
            from obratka.report.artifacts import (
                ReclassificationSample,
                Step22Artifact,
                make_stage_stats,
            )

            before_by_id = {q.review_id: q for q in queue}

            def _to_sample(a: ReviewAnalysis, text: str = "") -> "ReviewAnalysisSample":
                from obratka.report.artifacts import ReviewAnalysisSample

                return ReviewAnalysisSample(
                    review_id=a.review_id,
                    text=text[:500],
                    overall_sentiment=a.overall_sentiment,
                    overall_confidence=a.overall_confidence,
                    aspects=[asp.model_dump() for asp in a.aspects],
                )

            confidence_deltas: list[float] = []
            sentiment_changes = 0
            topic_changes = 0
            samples: list[ReclassificationSample] = []

            for after in final:
                before_item = before_by_id.get(after.review_id)
                if before_item is None:
                    continue
                before = before_item.initial_analysis
                delta = after.overall_confidence - before.overall_confidence
                confidence_deltas.append(delta)
                flipped = before.overall_sentiment != after.overall_sentiment
                if flipped:
                    sentiment_changes += 1
                before_topics = {a.topic for a in before.aspects}
                after_topics = {a.topic for a in after.aspects}
                topics_changed = before_topics != after_topics
                if topics_changed:
                    topic_changes += 1
                samples.append(
                    ReclassificationSample(
                        review_id=after.review_id,
                        text=before_item.text[:500],
                        before=_to_sample(before, before_item.text),
                        after=_to_sample(after, before_item.text),
                        improvement=round(delta, 3),
                        flipped_sentiment=flipped,
                    )
                )

            changed = [s for s in samples if s.flipped_sentiment]
            rest = [s for s in samples if not s.flipped_sentiment]
            limit = min(max(collector.max_samples, 10), 20)
            chosen = changed[:limit]
            if len(chosen) < limit and rest:
                chosen.extend(random.sample(rest, min(limit - len(chosen), len(rest))))

            avg_delta = (
                sum(confidence_deltas) / len(confidence_deltas)
                if confidence_deltas
                else 0.0
            )
            collector.record_step22(
                Step22Artifact(
                    stats=make_stage_stats(
                        "step22", started_at, cost_usd=cost_after - cost_before
                    ),
                    queue_size=len(queue),
                    reclassified_count=len(results),
                    still_low_conf_count=still_low,
                    confidence_delta_avg=round(avg_delta, 3),
                    sentiment_changes=sentiment_changes,
                    topic_changes=topic_changes,
                    samples=chosen,
                )
            )

        return final
