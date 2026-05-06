"""Async-оркестратор пайплайна. Вариант B: реализованы шаги 0-4 (кроме 1).
"""

from __future__ import annotations

import asyncio
import json
import time
from datetime import datetime
from dataclasses import dataclass
from typing import TYPE_CHECKING
from uuid import uuid4

from obratka.config import Settings, get_settings
from obratka.llm.client import LLMClient
from obratka.llm.schemas import (
    BusinessContext,
    CoreKPI,
    FakeStats,
    LoyaltyIndex,
    PipelineResult,
    RawReview,
)
from obratka.logging_setup import get_logger, setup_logging
from obratka.observability.phoenix_setup import get_current_trace_id, setup_phoenix
from obratka.observability.spans import step_span
from obratka.runner import BatchRunner

from obratka.steps.step0_normalize import normalize_batch
from obratka.steps.step05_translate import step05_translate_all
from obratka.steps.step2_topics import BASE_TOPICS, step2_run
from obratka.steps.step21_cluster import apply_topic_map, cluster_topics
from obratka.steps.step22_reclassify import step22_reclassify_all
from obratka.steps.step3_kpi import aggregate_kpi
from obratka.steps.step4_recommend import generate_recommendations

if TYPE_CHECKING:
    from obratka.report.artifacts import ArtifactCollector

log = get_logger(__name__)


@dataclass
class PipelineComponents:
    settings: Settings
    llm: LLMClient | None
    runner: BatchRunner

    @classmethod
    def build(
        cls,
        settings: Settings | None = None,
        *,
        dry_run: bool = False,
        llm: LLMClient | None = None,
    ) -> "PipelineComponents":
        s = settings or get_settings()
        if llm is None and not dry_run:
            llm = LLMClient(api_key=s.openrouter_api_key, models=s.models)
        runner = BatchRunner(max_concurrency=s.pipeline.max_concurrency)
        return cls(settings=s, llm=llm, runner=runner)

async def run_pipeline(
    reviews: list[dict] | list[RawReview],
    business_id: int,
    settings: Settings | None = None,
    *,
    dry_run: bool = False,
    llm: LLMClient | None = None,
) -> PipelineResult:
    s = settings or get_settings()
    setup_logging(level=s.log_level, logs_dir=s.logs_dir)
    setup_phoenix(s)
    components = PipelineComponents.build(s, dry_run=dry_run, llm=llm)

    run_id = str(uuid4())
    started = time.perf_counter()
    orch_log = log.bind(step="orchestrator")

    async with step_span(
        "pipeline",
        **{
            "obratka.run_id": run_id,
            "business_id": business_id,
            "total_reviews": len(reviews),
            "dry_run": dry_run,
        },
    ) as pipeline_span:
        trace_id = get_current_trace_id()
        collector = None
        if s.report.enabled:
            from obratka.report.artifacts import ArtifactCollector

            collector = ArtifactCollector(
                run_id=run_id,
                business_id=business_id,
                max_samples=s.report.max_samples_per_step,
            )

        with log.contextualize(
            run_id=run_id,
            run_id_short=run_id[:8],
            business_id=business_id,
            trace_id=trace_id or "-",
        ):
            result: PipelineResult | None = None
            try:
                result = await _run_pipeline_inner(
                    reviews=reviews,
                    business_id=business_id,
                    components=components,
                    settings=s,
                    dry_run=dry_run,
                    run_id=run_id,
                    collector=collector,
                    orch_log=orch_log,
                )
                pipeline_span.set_attribute("total_cost_usd", result.total_cost_usd)
                pipeline_span.set_attribute(
                    "pain_points_count", len(result.pain_points)
                )
                return result
            finally:
                if result is not None and collector is not None:
                    artifacts = collector.finalize(
                        total_cost_usd=result.total_cost_usd,
                        phoenix_trace_id=trace_id,
                    )
                    try:
                        from obratka.report.builder import render_report

                        report_path = render_report(
                            result,
                            artifacts,
                            output_dir=s.report.output_dir,
                            open_in_browser=s.report.open_in_browser,
                            phoenix_ui_url_template=s.phoenix.ui_url_template,
                            phoenix_project=s.phoenix.project_name,
                        )
                        orch_log.info("Report generated", path=str(report_path))
                    except Exception as e:
                        orch_log.warning("Report generation failed", error=str(e))


async def _run_pipeline_inner(
    *,
    reviews: list[dict] | list[RawReview],
    business_id: int,
    components: PipelineComponents,
    settings: Settings,
    dry_run: bool,
    run_id: str,
    collector: ArtifactCollector | None,
    orch_log,
) -> PipelineResult:
    started = time.perf_counter()
    s = settings

    orch_log.info(
        "Pipeline start",
        total_reviews=len(reviews),
        dry_run=dry_run,
        max_concurrency=components.runner.max_concurrency,
    )

    raw = [r if isinstance(r, RawReview) else RawReview.model_validate(r) for r in reviews]

    # Шаг 0
    normalized = normalize_batch(raw, collector=collector)
    
    if dry_run:
        orch_log.info("Dry-run: skipping LLM steps")
        now = datetime.now()
        return PipelineResult(
            run_id=run_id, business_id=business_id, generated_at=now,
            period_start=now, period_end=now,
            core_kpi=CoreKPI(avg_rating=0, rating_dynamics={}, negative_share=0, positive_share=0, mixed_share=0, total_reviews=len(raw), period_start=now, period_end=now),
            loyalty=LoyaltyIndex(score=0, promoters_pct=0, passives_pct=0, detractors_pct=0),
            pain_points=[], trends={}, fake_stats=FakeStats(total_collected=len(raw), fakes_detected=0, fakes_share=0, suspicious_authors=0),
            low_confidence_count=0, recommendations=[], total_cost_usd=0
        )

    if components.llm is None:
        raise RuntimeError("LLM client is required when dry_run=False")

    # Шаг 0.5
    translations = await step05_translate_all(
        normalized, components.runner, components.llm, collector=collector
    )
    
    # Шаг 1 (пропущен по задаче)

    # Шаг 2
    high_conf, low_conf_queue = await step2_run(
        normalized, translations, components.runner, components.llm, 
        batch_size=s.pipeline.step2_batch_size, 
        low_conf_threshold=s.pipeline.step2_low_conf_threshold,
        collector=collector,
    )
    
    # Сбор уникальных тем для кластеризации
    all_topics = set()
    for a in high_conf:
        for asp in a.aspects:
            if asp.is_freeform or asp.topic not in BASE_TOPICS:
                all_topics.add(asp.topic)
    for q in low_conf_queue:
        for asp in q.initial_analysis.aspects:
            if asp.is_freeform or asp.topic not in BASE_TOPICS:
                all_topics.add(asp.topic)
                
    # Шаг 2.1
    topic_map = await cluster_topics(
        list(all_topics), BASE_TOPICS, components.llm, collector=collector
    )
    available_topics = BASE_TOPICS + topic_map.canonical_topics
    
    # Шаг 2.2
    reclassified = await step22_reclassify_all(
        low_conf_queue, available_topics, components.llm, collector=collector
    )
    
    # Merge results
    final_analyses = high_conf + reclassified
    
    # Применяем TopicMap к high_conf (reclassified уже используют available_topics)
    final_analyses = apply_topic_map(final_analyses, topic_map)
    
    # Шаг 3
    pipeline_result = aggregate_kpi(
        final_analyses,
        normalized,
        business_id,
        weighting=s.pipeline.weighting,
        collector=collector,
    )
    pipeline_result.run_id = run_id
    
    # Шаг 4
    ctx = BusinessContext(business_id=business_id, business_type="unknown", name="Business", location=None)
    recs_out = await generate_recommendations(
        pipeline_result, ctx, components.llm, collector=collector
    )
    
    pipeline_result.recommendations = recs_out.recommendations
    pipeline_result.total_cost_usd = components.llm.total_cost
    
    duration = time.perf_counter() - started
    orch_log.info(
        "Pipeline done",
        total_cost_usd=pipeline_result.total_cost_usd,
        total_duration_s=round(duration, 3),
    )
    return pipeline_result

def main() -> None:
    import argparse
    from obratka.io.loaders import load_reviews

    p = argparse.ArgumentParser(prog="obratka.orchestrator")
    p.add_argument("--input", required=True, help="Путь к JSON со списком отзывов.")
    p.add_argument("--business-id", type=int, required=True)
    p.add_argument("--output", default=None, help="Куда сохранить результат (JSON).")
    p.add_argument(
        "--dry-run",
        action="store_true",
        help="Запустить без LLM (только нормализация).",
    )
    args = p.parse_args()

    reviews = load_reviews(args.input)

    result = asyncio.run(
        run_pipeline(reviews, business_id=args.business_id, dry_run=args.dry_run)
    )

    payload = result.model_dump(mode="json")
    
    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2, default=str)
    else:
        print(json.dumps(payload, ensure_ascii=False, indent=2, default=str))

if __name__ == "__main__":
    main()
