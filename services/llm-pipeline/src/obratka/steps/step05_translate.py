from __future__ import annotations

import random
from datetime import datetime
from typing import TYPE_CHECKING

from obratka.llm.client import LLMClient
from obratka.llm.schemas import NormalizedReview, TranslatedReview, TranslationOutput
from obratka.logging_setup import get_logger
from obratka.observability.spans import batch_span, step_span
from obratka.runner import BatchRunner

if TYPE_CHECKING:
    from obratka.report.artifacts import ArtifactCollector

log = get_logger(__name__)


async def translate_review(
    review: NormalizedReview,
    llm: LLMClient,
) -> TranslatedReview:
    prompt = f"Переведи этот отзыв:\n\n{review.text_normalized}"
    sys_prompt = (
        "Ты — переводчик коротких отзывов о бизнесах (рестораны, магазины, услуги).\n"
        "Переводи текст на русский язык, сохраняя:\n"
        "— тональность (позитив/негатив/нейтрал),\n"
        "— сленг и эмоциональную окраску,\n"
        "— конкретные детали (имена сотрудников, блюд, услуг).\n\n"
        "Не добавляй ничего от себя. Не комментируй перевод.\n"
        'Верни строго JSON: {"text_ru": "..."}.'
    )

    try:
        async with batch_span("step05", review.review_id, 1):
            content, _ = await llm.complete(
                model="translate",
                response_model=TranslationOutput,
                messages=[
                    {"role": "system", "content": sys_prompt},
                    {"role": "user", "content": prompt},
                ],
                request_id=review.review_id,
            )
        return TranslatedReview(
            review_id=review.review_id,
            text_translated=content.text_ru,
            source_lang=review.lang,
        )
    except Exception as e:
        log.error("Translation failed", review_id=review.review_id, error=str(e))
        return TranslatedReview(
            review_id=review.review_id,
            text_translated=review.text_normalized,
            source_lang=review.lang,
        )


async def step05_translate_all(
    reviews: list[NormalizedReview],
    runner: BatchRunner,
    llm: LLMClient,
    *,
    collector: "ArtifactCollector | None" = None,
) -> dict[str, TranslatedReview]:
    started_at = datetime.now()
    to_translate = [
        r for r in reviews if r.lang not in ("ru", "unknown") and r.lang_confidence >= 0.6
    ]
    async with step_span("step05", reviews_count=len(reviews), non_ru_count=len(to_translate)) as span:
        log.info("Translation step start", non_ru_count=len(to_translate))

        if not to_translate:
            span.set_attribute("translated_count", 0)
            if collector is not None:
                from obratka.report.artifacts import Step05Artifact, make_stage_stats

                collector.record_step05(
                    Step05Artifact(
                        stats=make_stage_stats("step05", started_at),
                        translated_count=0,
                        samples=[],
                    )
                )
            return {}

        cost_before = llm.total_cost if llm is not None else 0.0
        results: list[TranslatedReview] = await runner.run_many(
            to_translate, worker=lambda r: translate_review(r, llm)
        )
        cost_after = llm.total_cost if llm is not None else 0.0

        log.info("Translation step done", translated=len(results))
        span.set_attribute("translated_count", len(results))
        span.set_attribute("cost_usd", cost_after - cost_before)

        out = {r.review_id: r for r in results}

        if collector is not None:
            from obratka.report.artifacts import (
                Step05Artifact,
                TranslationSample,
                make_stage_stats,
            )

            review_by_id = {r.review_id: r for r in to_translate}
            sample_n = min(collector.max_samples, 5, len(results))
            chosen = random.sample(results, sample_n) if sample_n > 0 else []
            samples = [
                TranslationSample(
                    review_id=t.review_id,
                    source_lang=t.source_lang,
                    text_original=review_by_id[t.review_id].text_normalized[:500],
                    text_ru=t.text_translated[:500],
                )
                for t in chosen
                if t.review_id in review_by_id
            ]
            failed = sum(
                1
                for t in results
                if t.text_translated == review_by_id.get(t.review_id, t).text_normalized
            )
            collector.record_step05(
                Step05Artifact(
                    stats=make_stage_stats(
                        "step05", started_at, cost_usd=cost_after - cost_before
                    ),
                    translated_count=len(results),
                    cached_count=0,
                    failed_count=failed,
                    samples=samples,
                ),
            )

        return out
