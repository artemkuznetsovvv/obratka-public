# Задача 11: Конфиг и переменные окружения

## Цель

Все настройки (API-ключи, цены моделей, лимиты, пороги) — в одном месте. Никаких хардкодов в коде шагов.

## Файлы

- `src/obratka/config.py`
- `.env.example`

## Стек

```toml
pydantic-settings = "^2.2"
python-dotenv = "^1.0"
```

## Структура конфига

```python
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

class PipelineConfig(BaseModel):
    # Параллелизм
    max_concurrency: int = 8
    step22_max_concurrency: int = 4   # сильная модель — ниже параллелизм

    # Шаг 2
    step2_batch_size: int = 12
    step2_low_conf_threshold: float = 0.5

    # Сетевое
    request_timeout_s: float = 60.0
    max_retries: int = 3
    rate_limit_rps: float = 20.0      # глобальный лимит на OpenRouter

    # Кэш
    author_cache_ttl_hours: int = 24

class PromptVersions(BaseModel):
    step05_translate: str = "step05-v1.0"
    step1_fakes: str = "step1-fakes-v1.0"
    step2_topics: str = "step2-topics-v1.0"
    step21_cluster: str = "step21-cluster-v1.0"
    step22_reclassify: str = "step22-reclassify-v1.0"
    step4_recommendations: str = "step4-rec-v1.0"

class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env",
        env_nested_delimiter="__",
    )

    # Секреты
    openrouter_api_key: str
    database_url: str

    # Метаданные
    app_env: str = "dev"           # dev | staging | prod
    log_level: str = "INFO"
    logs_dir: str = "logs"

    # Конфиг моделей
    models: dict[str, ModelConfig] = Field(default_factory=lambda: {
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
    })

    pipeline: PipelineConfig = PipelineConfig()
    prompts: PromptVersions = PromptVersions()


# Глобальный singleton
settings = Settings()
```

## .env.example

```dotenv
# OpenRouter API key
OPENROUTER_API_KEY=sk-or-v1-...

# PostgreSQL connection
DATABASE_URL=postgresql+asyncpg://user:pass@localhost:5432/obratka

# Окружение
APP_ENV=dev
LOG_LEVEL=INFO
LOGS_DIR=logs

# Опциональные оверрайды через nested env vars:
# PIPELINE__MAX_CONCURRENCY=12
# PIPELINE__STEP2_BATCH_SIZE=15
# PIPELINE__STEP2_LOW_CONF_THRESHOLD=0.6
```

## Расчёт стоимости вызова

```python
def calc_cost(usage: UsageInfo, pricing: ModelPricing) -> float:
    return (
        usage.prompt_tokens * pricing.input_per_m / 1_000_000
        + usage.completion_tokens * pricing.output_per_m / 1_000_000
    )
```

Эта функция используется в `LLMClient` после каждого вызова, чтобы посчитать `cost_usd` и записать в лог.

## Критерии готовности

- [ ] Все цены и пороги читаются из конфига, в коде шагов хардкодов нет.
- [ ] При отсутствии `OPENROUTER_API_KEY` в `.env` — Settings бросают ошибку при импорте.
- [ ] `prompts.step2_topics` используется как `prompt_version` при записи в БД и кэш.
- [ ] Тест: подмена `PIPELINE__STEP2_BATCH_SIZE=15` через env работает.

## Подсказки

- Чтобы менять цены без рестарта — можно вынести `models` в отдельный YAML и читать его при старте.
- В проде секреты лучше брать из vault, а не из `.env`.
