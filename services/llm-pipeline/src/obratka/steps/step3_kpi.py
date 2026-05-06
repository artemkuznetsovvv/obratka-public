"""Шаг 3 — алгоритмическая агрегация KPI.

Особенности task 12:
- raw / weighted / fresh версии CoreKPI и LoyaltyIndex,
- веса по time-decay (см. obratka.utils.weighting),
- pain points с avg_age_days, weighted_mention_count, sample_dates.
"""

from __future__ import annotations

from collections import defaultdict
from datetime import datetime, timedelta, timezone
from typing import TYPE_CHECKING

import pandas as pd

from obratka.config import WeightingConfig, WeightingStrategy
from obratka.llm.schemas import (
    CoreKPI,
    FakeStats,
    LoyaltyIndex,
    NormalizedReview,
    PainPoint,
    PipelineResult,
    PositivePoint,
    ReviewAnalysis,
)
from obratka.logging_setup import get_logger
from obratka.utils.weighting import compute_weights

if TYPE_CHECKING:
    from obratka.report.artifacts import ArtifactCollector

log = get_logger(__name__)


_NEG = ("негативный", "очень негативный")
_POS = ("позитивный", "очень позитивный")


def _to_utc_naive(dt: datetime) -> datetime:
    if dt.tzinfo is None:
        return dt
    return dt.astimezone(timezone.utc).replace(tzinfo=None)


def _share(filtered: float, total: float) -> float:
    return (filtered / total) if total > 0 else 0.0


def _compute_core_kpi(
    df: pd.DataFrame,
    *,
    weights: dict[str, float] | None,
    period_start: datetime,
    period_end: datetime,
) -> CoreKPI:
    """Считает CoreKPI на отфильтрованном df. Если weights=None — без весов."""
    if df.empty:
        return CoreKPI(
            avg_rating=0.0,
            rating_dynamics={},
            negative_share=0.0,
            positive_share=0.0,
            mixed_share=0.0,
            total_reviews=0,
            period_start=period_start,
            period_end=period_end,
        )

    if weights is None:
        w = pd.Series(1.0, index=df.index)
    else:
        w = df["review_id"].map(lambda rid: weights.get(rid, 1.0))

    total_w = float(w.sum())

    is_neg = df["overall_sentiment"].isin(_NEG)
    is_pos = df["overall_sentiment"].isin(_POS)
    is_mix = ~(is_neg | is_pos)

    neg_share = float((w * is_neg).sum() / total_w) if total_w > 0 else 0.0
    pos_share = float((w * is_pos).sum() / total_w) if total_w > 0 else 0.0
    mix_share = float((w * is_mix).sum() / total_w) if total_w > 0 else 0.0

    if "stars" in df.columns and df["stars"].notna().any():
        valid = df["stars"].notna()
        sw = w[valid]
        if float(sw.sum()) > 0:
            avg_rating = float((df.loc[valid, "stars"] * sw).sum() / sw.sum())
        else:
            avg_rating = 0.0
    else:
        avg_rating = 0.0

    return CoreKPI(
        avg_rating=avg_rating,
        rating_dynamics={},
        negative_share=neg_share,
        positive_share=pos_share,
        mixed_share=mix_share,
        total_reviews=int(len(df)),
        period_start=period_start,
        period_end=period_end,
    )


def _compute_loyalty(
    df: pd.DataFrame,
    *,
    weights: dict[str, float] | None,
) -> LoyaltyIndex:
    if df.empty:
        return LoyaltyIndex(score=0.0, promoters_pct=0.0, passives_pct=0.0, detractors_pct=0.0)

    if weights is None:
        w = pd.Series(1.0, index=df.index)
    else:
        w = df["review_id"].map(lambda rid: weights.get(rid, 1.0))

    total_w = float(w.sum())
    if total_w <= 0:
        return LoyaltyIndex(score=0.0, promoters_pct=0.0, passives_pct=0.0, detractors_pct=0.0)

    is_pro = df["overall_sentiment"].isin(_POS)
    is_det = df["overall_sentiment"].isin(_NEG)
    is_pas = ~(is_pro | is_det)

    pro = float((w * is_pro).sum() / total_w)
    det = float((w * is_det).sum() / total_w)
    pas = float((w * is_pas).sum() / total_w)

    return LoyaltyIndex(
        score=(pro - det) * 100.0,
        promoters_pct=pro,
        passives_pct=pas,
        detractors_pct=det,
    )


def _compute_pain_points(
    analyses: list[ReviewAnalysis],
    raw_reviews: list[NormalizedReview],
    weights: dict[str, float],
    reference_date: datetime,
    fresh_window_days: int,
) -> list[PainPoint]:
    """Болевые точки с raw/weighted метриками и avg_age_days."""
    reference_date = _to_utc_naive(reference_date)
    review_dates = {r.review_id: _to_utc_naive(r.date) for r in raw_reviews}

    topic_stats: dict[str, dict] = defaultdict(
        lambda: {
            "total": 0,
            "neg": 0,
            "pos": 0,
            "weighted_total": 0.0,
            "weighted_neg": 0.0,
            "weighted_pos": 0.0,
            "fragments": [],
            "pos_fragments": [],
            "dates": [],
            "pos_dates": [],
            "low_conf": 0,
            "neg_dates_recent": [],
            "neg_dates_prev": [],
        }
    )
    fresh_cutoff = reference_date - timedelta(days=fresh_window_days)
    prev_cutoff = reference_date - timedelta(days=fresh_window_days * 2)

    for a in analyses:
        rdate = review_dates.get(a.review_id)
        rweight = weights.get(a.review_id, 1.0)
        for aspect in a.aspects:
            t = aspect.topic
            stats = topic_stats[t]
            stats["total"] += 1
            stats["weighted_total"] += rweight
            is_neg = aspect.sentiment in _NEG
            is_pos = aspect.sentiment in _POS
            if is_neg:
                stats["neg"] += 1
                stats["weighted_neg"] += rweight
                if len(stats["fragments"]) < 3:
                    stats["fragments"].append(aspect.fragment)
                if rdate is not None:
                    if len(stats["dates"]) < 10:
                        stats["dates"].append(rdate)
                    if rdate >= fresh_cutoff:
                        stats["neg_dates_recent"].append(rdate)
                    elif rdate >= prev_cutoff:
                        stats["neg_dates_prev"].append(rdate)
            if is_pos:
                stats["pos"] += 1
                stats["weighted_pos"] += rweight
                if len(stats["pos_fragments"]) < 3:
                    stats["pos_fragments"].append(aspect.fragment)
                if rdate is not None and len(stats["pos_dates"]) < 10:
                    stats["pos_dates"].append(rdate)
            if a.low_confidence_final:
                stats["low_conf"] += 1

    out: list[PainPoint] = []
    for t, s in topic_stats.items():
        if s["total"] < 5:
            continue
        neg_share_raw = s["neg"] / s["total"]
        neg_share_weighted = (
            s["weighted_neg"] / s["weighted_total"] if s["weighted_total"] > 0 else 0.0
        )
        # Растущая боль: сравнение свежих 30 дней с предыдущими 30
        recent_n = len(s["neg_dates_recent"])
        prev_n = len(s["neg_dates_prev"])
        if prev_n > 0:
            growth = ((recent_n - prev_n) / prev_n) * 100.0
        elif recent_n > 0:
            growth = float("inf")  # был ноль, стало > 0
        else:
            growth = None
        # Условие болевой точки:
        # - высокая доля негатива по теме,
        # - или заметный абсолютный объём негатива в реальных отзывах,
        # - или свежий рост. Иначе темы с большим числом позитивных упоминаний
        #   скрывают важные негативные сигналы.
        is_pain = (
            neg_share_weighted > 0.20
            or s["neg"] >= 3
            or s["weighted_neg"] >= 2.0
            or (growth is not None and growth > 50.0)
        )
        if not is_pain:
            continue
        if s["dates"]:
            avg_age = sum(
                (reference_date - d).total_seconds() / 86400.0 for d in s["dates"]
            ) / len(s["dates"])
        else:
            avg_age = None
        # growth=inf не сериализуется — превратим в большое число
        growth_serialized: float | None
        if growth is None:
            growth_serialized = None
        elif growth == float("inf"):
            growth_serialized = 999.0
        else:
            growth_serialized = round(growth, 1)
        out.append(
            PainPoint(
                topic=t,
                negative_share=neg_share_raw,
                negative_share_weighted=neg_share_weighted,
                negative_mention_count=int(s["neg"]),
                weighted_negative_mention_count=round(float(s["weighted_neg"]), 3),
                mention_count=int(s["total"]),
                weighted_mention_count=round(float(s["weighted_total"]), 3),
                growth_pct=growth_serialized,
                growth_pct_30d=growth_serialized,
                sample_fragments=list(s["fragments"]),
                sample_dates=list(s["dates"]),
                avg_age_days=round(float(avg_age), 1) if avg_age is not None else None,
                is_low_confidence_dominant=(s["low_conf"] / s["total"]) > 0.5,
            )
        )
    out.sort(
        key=lambda p: (p.negative_share_weighted or 0.0) * (p.weighted_mention_count or 0.0),
        reverse=True,
    )
    return out


def _compute_positive_points(
    analyses: list[ReviewAnalysis],
    raw_reviews: list[NormalizedReview],
    weights: dict[str, float],
    reference_date: datetime,
) -> list[PositivePoint]:
    """Сильные стороны бизнеса по позитивным аспектам."""
    reference_date = _to_utc_naive(reference_date)
    review_dates = {r.review_id: _to_utc_naive(r.date) for r in raw_reviews}

    topic_stats: dict[str, dict] = defaultdict(
        lambda: {
            "total": 0,
            "pos": 0,
            "weighted_total": 0.0,
            "weighted_pos": 0.0,
            "fragments": [],
            "dates": [],
            "low_conf": 0,
        }
    )

    for a in analyses:
        rdate = review_dates.get(a.review_id)
        rweight = weights.get(a.review_id, 1.0)
        for aspect in a.aspects:
            stats = topic_stats[aspect.topic]
            stats["total"] += 1
            stats["weighted_total"] += rweight
            if aspect.sentiment in _POS:
                stats["pos"] += 1
                stats["weighted_pos"] += rweight
                if len(stats["fragments"]) < 3:
                    stats["fragments"].append(aspect.fragment)
                if rdate is not None and len(stats["dates"]) < 10:
                    stats["dates"].append(rdate)
            if a.low_confidence_final:
                stats["low_conf"] += 1

    out: list[PositivePoint] = []
    for topic, s in topic_stats.items():
        if s["total"] < 5:
            continue
        pos_share_raw = s["pos"] / s["total"]
        pos_share_weighted = (
            s["weighted_pos"] / s["weighted_total"] if s["weighted_total"] > 0 else 0.0
        )
        is_strength = pos_share_weighted > 0.50 or s["pos"] >= 5
        if not is_strength:
            continue
        if s["dates"]:
            avg_age = sum(
                (reference_date - d).total_seconds() / 86400.0 for d in s["dates"]
            ) / len(s["dates"])
        else:
            avg_age = None
        out.append(
            PositivePoint(
                topic=topic,
                positive_share=pos_share_raw,
                positive_share_weighted=pos_share_weighted,
                positive_mention_count=int(s["pos"]),
                weighted_positive_mention_count=round(float(s["weighted_pos"]), 3),
                mention_count=int(s["total"]),
                weighted_mention_count=round(float(s["weighted_total"]), 3),
                sample_fragments=list(s["fragments"]),
                sample_dates=list(s["dates"]),
                avg_age_days=round(float(avg_age), 1) if avg_age is not None else None,
                is_low_confidence_dominant=(s["low_conf"] / s["total"]) > 0.5,
            )
        )

    out.sort(
        key=lambda p: (p.positive_share_weighted or 0.0)
        * (p.weighted_mention_count or 0.0),
        reverse=True,
    )
    return out


def aggregate_kpi(
    analyses: list[ReviewAnalysis],
    raw_reviews: list[NormalizedReview],
    business_id: int,
    *,
    weighting: WeightingConfig | None = None,
    reference_date: datetime | None = None,
    collector: "ArtifactCollector | None" = None,
) -> PipelineResult:
    """Агрегирует raw / weighted / fresh KPI + болевые точки."""
    log_step = log.bind(step="step3")
    log_step.info("Step 3 start", analyses=len(analyses), reviews=len(raw_reviews))

    cfg = weighting or WeightingConfig()
    ref_date = _to_utc_naive(reference_date or datetime.now(timezone.utc))

    df_reviews = pd.DataFrame([r.model_dump() for r in raw_reviews])
    df_analyses = pd.DataFrame([a.model_dump() for a in analyses])
    if not df_reviews.empty and "date" in df_reviews.columns:
        df_reviews["date"] = pd.to_datetime(df_reviews["date"], utc=True).dt.tz_localize(None)

    if df_reviews.empty or df_analyses.empty:
        df = pd.DataFrame(columns=["review_id", "overall_sentiment", "stars", "date"])
    else:
        df = pd.merge(df_reviews, df_analyses, on="review_id", how="inner")

    period_start = df["date"].min() if not df.empty else ref_date
    period_end = df["date"].max() if not df.empty else ref_date

    # Веса
    if cfg.enabled and cfg.strategy != WeightingStrategy.none:
        weights = compute_weights(
            review_dates={r.review_id: r.date for r in raw_reviews},
            reference_date=ref_date,
            strategy=cfg.strategy.value,
            half_life_days=cfg.half_life_days,
            weight_floor=cfg.weight_floor,
            fresh_window_days=cfg.fresh_window_days,
        )
    else:
        weights = {r.review_id: 1.0 for r in raw_reviews}

    # raw — без весов (всегда)
    raw_kpi = _compute_core_kpi(df, weights=None, period_start=period_start, period_end=period_end)
    raw_loyalty = _compute_loyalty(df, weights=None)

    # weighted — с весами (None если выключено)
    if cfg.enabled and cfg.strategy != WeightingStrategy.none:
        weighted_kpi = _compute_core_kpi(
            df, weights=weights, period_start=period_start, period_end=period_end
        )
        weighted_loyalty = _compute_loyalty(df, weights=weights)
    else:
        weighted_kpi = None
        weighted_loyalty = None

    # fresh — последние fresh_window_days, без весов
    fresh_cutoff = ref_date - timedelta(days=cfg.fresh_window_days)
    if df.empty:
        fresh_df = df
    else:
        fresh_df = df[df["date"] >= fresh_cutoff]
    if not fresh_df.empty:
        fresh_kpi = _compute_core_kpi(
            fresh_df,
            weights=None,
            period_start=fresh_cutoff,
            period_end=period_end,
        )
        fresh_loyalty = _compute_loyalty(fresh_df, weights=None)
    else:
        fresh_kpi = None
        fresh_loyalty = None

    pain_points = _compute_pain_points(
        analyses, raw_reviews, weights, ref_date, cfg.fresh_window_days
    )
    positive_points = _compute_positive_points(analyses, raw_reviews, weights, ref_date)

    fake_stats = FakeStats(
        total_collected=len(raw_reviews),
        fakes_detected=0,
        fakes_share=0.0,
        suspicious_authors=0,
    )

    res = PipelineResult(
        run_id="run",
        business_id=business_id,
        generated_at=ref_date,
        period_start=period_start,
        period_end=period_end,
        core_kpi=raw_kpi,
        core_kpi_weighted=weighted_kpi,
        core_kpi_fresh=fresh_kpi,
        loyalty=raw_loyalty,
        loyalty_weighted=weighted_loyalty,
        loyalty_fresh=fresh_loyalty,
        pain_points=pain_points,
        positive_points=positive_points,
        trends={},
        fake_stats=fake_stats,
        low_confidence_count=sum(1 for a in analyses if a.low_confidence_final),
        recommendations=[],
        total_cost_usd=0.0,
    )

    log_step.info(
        "Step 3 done",
        raw_neg_share=round(raw_kpi.negative_share, 3),
        weighted_neg_share=round(weighted_kpi.negative_share, 3) if weighted_kpi else None,
        fresh_neg_share=round(fresh_kpi.negative_share, 3) if fresh_kpi else None,
        pain_points=len(pain_points),
        positive_points=len(positive_points),
    )

    if collector is not None:
        from obratka.report.artifacts import Step3Artifact, make_stage_stats

        art = Step3Artifact(
            stats=make_stage_stats("step3", ref_date),
            core_kpi=raw_kpi.model_dump(mode="json"),
            loyalty=raw_loyalty.model_dump(mode="json"),
            pain_points_top=[p.model_dump(mode="json") for p in pain_points[:10]],
            positive_points_top=[
                p.model_dump(mode="json") for p in positive_points[:10]
            ],
            trends={},
            fake_stats=fake_stats.model_dump(mode="json"),
            weighted_kpi=weighted_kpi.model_dump(mode="json") if weighted_kpi else None,
            fresh_window_kpi=fresh_kpi.model_dump(mode="json") if fresh_kpi else None,
        )
        collector.record_step3(art)

    return res
