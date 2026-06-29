"""Pydantic-схемы пайплайна."""

from __future__ import annotations

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field


class RawReview(BaseModel):
    review_id: str
    author_id: str | None = None
    text: str
    stars: int | None = None
    date: datetime
    source: str  # "yandex" | "2gis" | "google"


class NormalizedReview(BaseModel):
    review_id: str
    author_id: str | None = None
    text_raw: str
    text_normalized: str
    text_hash: str
    lang: str
    lang_confidence: float
    stars: int | None = None
    date: datetime
    source: str
    is_empty: bool = False
    text_translated: str | None = None

class TranslatedReview(BaseModel):
    review_id: str
    text_translated: str
    source_lang: str

class TranslationOutput(BaseModel):
    text_ru: str = Field(..., description="Перевод на русский язык")

SENTIMENT = Literal[
    "очень негативный",
    "негативный",
    "нейтральный",
    "позитивный",
    "очень позитивный",
    "смешанный",
]

class Aspect(BaseModel):
    topic: str
    sentiment: SENTIMENT
    confidence: float = Field(ge=0.0, le=1.0)
    fragment: str
    is_freeform: bool = False

class ReviewAnalysis(BaseModel):
    review_id: str
    is_mixed: bool
    overall_sentiment: SENTIMENT
    overall_confidence: float = Field(ge=0.0, le=1.0)
    aspects: list[Aspect]
    low_confidence_final: bool = False

class BatchOutput(BaseModel):
    items: list[ReviewAnalysis]

class LowConfItem(BaseModel):
    review_id: str
    text: str
    initial_analysis: ReviewAnalysis
    reason: str

class ReclassifyInput(BaseModel):
    review_id: str
    text: str
    initial_aspects: list[str]
    available_topics: list[str]

class ReclassifyOutput(BaseModel):
    review_id: str
    is_mixed: bool
    overall_sentiment: SENTIMENT
    overall_confidence: float
    aspects: list[Aspect]
    low_confidence_final: bool

class TopicMap(BaseModel):
    mapping: dict[str, str] = Field(default_factory=dict)
    canonical_topics: list[str] = Field(default_factory=list)

class CoreKPI(BaseModel):
    avg_rating: float
    rating_dynamics: dict[str, float]
    negative_share: float
    positive_share: float
    mixed_share: float
    total_reviews: int
    period_start: datetime
    period_end: datetime

class LoyaltyIndex(BaseModel):
    score: float
    promoters_pct: float
    passives_pct: float
    detractors_pct: float

class PainPoint(BaseModel):
    topic: str
    negative_share: float
    negative_share_weighted: float | None = None
    negative_mention_count: int | None = None
    weighted_negative_mention_count: float | None = None
    mention_count: int
    weighted_mention_count: float | None = None
    growth_pct: float | None = None
    growth_pct_30d: float | None = None
    sample_fragments: list[str]
    sample_dates: list[datetime] = Field(default_factory=list)
    avg_age_days: float | None = None
    is_low_confidence_dominant: bool = False

class PositivePoint(BaseModel):
    topic: str
    positive_share: float
    positive_share_weighted: float | None = None
    positive_mention_count: int | None = None
    weighted_positive_mention_count: float | None = None
    mention_count: int
    weighted_mention_count: float | None = None
    sample_fragments: list[str]
    sample_dates: list[datetime] = Field(default_factory=list)
    avg_age_days: float | None = None
    is_low_confidence_dominant: bool = False

class TrendBin(BaseModel):
    start: datetime
    end: datetime
    avg_rating: float
    review_count: int
    negative_share: float

class TrendData(BaseModel):
    period: str
    bins: list[TrendBin]

class FakeStats(BaseModel):
    total_collected: int
    fakes_detected: int
    fakes_share: float
    suspicious_authors: int

RecType = Literal["strategic", "tactical", "communication"]

class Recommendation(BaseModel):
    type: RecType
    priority: int = Field(ge=1, le=5)
    topic: str | None
    title: str
    body: str
    expected_impact: str
    evidence: list[str]

class RecommendationsOutput(BaseModel):
    summary: str
    recommendations: list[Recommendation]

class PipelineResult(BaseModel):
    run_id: str
    business_id: int
    generated_at: datetime
    period_start: datetime
    period_end: datetime
    core_kpi: CoreKPI
    core_kpi_weighted: CoreKPI | None = None
    core_kpi_fresh: CoreKPI | None = None
    loyalty: LoyaltyIndex
    loyalty_weighted: LoyaltyIndex | None = None
    loyalty_fresh: LoyaltyIndex | None = None
    pain_points: list[PainPoint]
    positive_points: list[PositivePoint] = Field(default_factory=list)
    trends: dict[str, TrendData]
    fake_stats: FakeStats
    low_confidence_count: int
    recommendations: list[Recommendation]
    total_cost_usd: float

class BusinessContext(BaseModel):
    business_id: int
    name: str | None
    business_type: str
    location: str | None
    branches_count: int = 1
    custom_notes: str | None = None
    # Опциональный бизнес-контекст из input.json (sibling к company_id).
    # Backward-compat: всё None, если бэк не прислал.
    business_category: str | None = None
    business_subcategory: str | None = None
    additional_context: str | None = None
