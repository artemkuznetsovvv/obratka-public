"""Конфиг и настройки. Цены, лимиты, пороги — всё здесь, без хардкодов в шагах."""

from __future__ import annotations

from enum import Enum

from pydantic import BaseModel, Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class ModelPricing(BaseModel):
    """Цены за 1M токенов в USD."""

    input_per_m: float
    output_per_m: float


class ModelConfig(BaseModel):
    openrouter_id: str
    pricing: ModelPricing
    max_tokens_default: int = 1024


class WeightingStrategy(str, Enum):
    none = "none"
    exp_half_life = "exp"
    linear_window = "linear"
    step = "step"


class WeightingConfig(BaseModel):
    strategy: WeightingStrategy = WeightingStrategy.exp_half_life
    half_life_days: float = 90.0
    weight_floor: float = 0.05
    fresh_window_days: int = 30
    enabled: bool = True


class PipelineConfig(BaseModel):
    max_concurrency: int = 8
    step22_max_concurrency: int = 4

    step2_batch_size: int = 12
    step2_low_conf_threshold: float = 0.5

    request_timeout_s: float = 60.0
    max_retries: int = 3
    rate_limit_rps: float = 20.0

    author_cache_ttl_hours: int = 24

    weighting: WeightingConfig = WeightingConfig()


class PromptVersions(BaseModel):
    step05_translate: str = "step05-v1.0"
    step1_fakes: str = "step1-fakes-v1.0"
    step2_topics: str = "step2-topics-v1.0"
    step21_cluster: str = "step21-cluster-v1.0"
    step22_reclassify: str = "step22-reclassify-v1.0"
    step4_recommendations: str = "step4-rec-v1.0"


def _default_models() -> dict[str, ModelConfig]:
    return {
        "translate": ModelConfig(
            openrouter_id="google/gemini-2.0-flash-001",
            pricing=ModelPricing(input_per_m=0.10, output_per_m=0.40),
        ),
        "fakes": ModelConfig(
            openrouter_id="openai/gpt-4o-mini",
            pricing=ModelPricing(input_per_m=0.15, output_per_m=0.60),
        ),
        "topics": ModelConfig(
            openrouter_id="openai/gpt-4o-mini",
            pricing=ModelPricing(input_per_m=0.15, output_per_m=0.60),
        ),
        "topics_strong": ModelConfig(
            openrouter_id="openai/gpt-4o",
            pricing=ModelPricing(input_per_m=2.50, output_per_m=10.00),
        ),
        "cluster": ModelConfig(
            openrouter_id="openai/gpt-4o-mini",
            pricing=ModelPricing(input_per_m=0.15, output_per_m=0.60),
        ),
        "recommendations": ModelConfig(
            openrouter_id="deepseek/deepseek-chat",
            pricing=ModelPricing(input_per_m=0.26, output_per_m=0.38),
        ),
    }


class PhoenixConfig(BaseModel):
    enabled: bool = False
    otlp_endpoint: str = "http://localhost:4318"
    project_name: str = "obratka-dev"
    api_key: str | None = None
    ui_url_template: str = "http://localhost:6006/projects/{project}/traces/{trace_id}"


class SeqConfig(BaseModel):
    """Отправка loguru-логов в Seq (CLEF поверх HTTP).

    Включается через SEQ__ENABLED=true + SEQ__URL=https://logs.example. На стэке
    Seq уже принимает .NET-сервисы через Serilog — Python шлёт в тот же формат.
    """

    enabled: bool = False
    url: str = ""
    api_key: str | None = None
    level: str = "INFO"
    timeout_s: float = 5.0
    service: str = "obratka-llm"


class ReportConfig(BaseModel):
    enabled: bool = False
    output_dir: str = "reports"
    open_in_browser: bool = False
    max_samples_per_step: int = 20
    chartjs_offline: bool = False


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        env_nested_delimiter="__",
        extra="ignore",
    )

    # OPENROUTER_API_KEY: формально обязателен, но default="" позволяет
    # запускать dry-run и unit-тесты без ключа. LLMClient.__init__ всё равно
    # упадёт ValueError при попытке реального LLM-вызова без ключа.
    openrouter_api_key: str = ""
    database_url: str = ""

    app_env: str = "dev"
    log_level: str = "INFO"
    logs_dir: str = "logs"

    models: dict[str, ModelConfig] = Field(default_factory=_default_models)
    pipeline: PipelineConfig = PipelineConfig()
    prompts: PromptVersions = PromptVersions()
    phoenix: PhoenixConfig = PhoenixConfig()
    seq: SeqConfig = SeqConfig()
    report: ReportConfig = ReportConfig()


def get_settings() -> Settings:
    """Фабрика, удобна для тестов (не singleton — каждый вызов перечитывает env)."""
    return Settings()
