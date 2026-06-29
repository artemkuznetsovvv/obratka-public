"""Time-decay веса для отзывов.

Свежие отзывы влияют на KPI сильнее старых.
Формула: weight = 0.5 ^ (age_days / half_life_days), min = weight_floor.
"""

from __future__ import annotations

from datetime import datetime, timezone


def _to_utc_naive(dt: datetime) -> datetime:
    """Normalize aware/naive datetimes for arithmetic."""
    if dt.tzinfo is None:
        return dt
    return dt.astimezone(timezone.utc).replace(tzinfo=None)


def time_decay_weight(
    review_date: datetime,
    reference_date: datetime,
    half_life_days: float = 90.0,
    weight_floor: float = 0.05,
) -> float:
    """Экспоненциальное затухание по половинному периоду."""
    review_date = _to_utc_naive(review_date)
    reference_date = _to_utc_naive(reference_date)
    age_days = max(0.0, (reference_date - review_date).total_seconds() / 86400.0)
    w = 0.5 ** (age_days / half_life_days)
    return max(weight_floor, w)


def linear_decay_weight(
    review_date: datetime,
    reference_date: datetime,
    window_days: float = 180.0,
    weight_floor: float = 0.05,
) -> float:
    """Линейное затухание от 1 до floor за window_days."""
    review_date = _to_utc_naive(review_date)
    reference_date = _to_utc_naive(reference_date)
    age_days = max(0.0, (reference_date - review_date).total_seconds() / 86400.0)
    if age_days >= window_days:
        return weight_floor
    w = 1.0 - (1.0 - weight_floor) * (age_days / window_days)
    return max(weight_floor, w)


def step_weight(
    review_date: datetime,
    reference_date: datetime,
    fresh_window_days: float = 30.0,
    weight_floor: float = 0.05,
) -> float:
    """Ступенчатый: 1.0 внутри окна, weight_floor вне."""
    review_date = _to_utc_naive(review_date)
    reference_date = _to_utc_naive(reference_date)
    age_days = max(0.0, (reference_date - review_date).total_seconds() / 86400.0)
    return 1.0 if age_days <= fresh_window_days else weight_floor


def compute_weights(
    review_dates: dict[str, datetime],
    reference_date: datetime,
    strategy: str = "exp",
    half_life_days: float = 90.0,
    weight_floor: float = 0.05,
    fresh_window_days: int = 30,
) -> dict[str, float]:
    """Считает веса для всех отзывов по выбранной стратегии.

    Returns:
        dict[review_id, weight]
    """
    if strategy == "none":
        return {rid: 1.0 for rid in review_dates}

    weights: dict[str, float] = {}
    for rid, dt in review_dates.items():
        if strategy == "exp":
            weights[rid] = time_decay_weight(dt, reference_date, half_life_days, weight_floor)
        elif strategy == "linear":
            weights[rid] = linear_decay_weight(dt, reference_date, half_life_days * 2, weight_floor)
        elif strategy == "step":
            weights[rid] = step_weight(dt, reference_date, fresh_window_days, weight_floor)
        else:
            weights[rid] = 1.0
    return weights
