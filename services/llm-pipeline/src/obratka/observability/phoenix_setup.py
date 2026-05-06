"""Инициализация Arize Phoenix для LLM-трейсинга.

Вызывается один раз в orchestrator перед стартом пайплайна.
При PHOENIX__ENABLED=false или недоступности Phoenix — пайплайн работает без трейсов.
"""

from __future__ import annotations

from obratka.logging_setup import get_logger

log = get_logger(__name__)

_tracer_provider = None


def setup_phoenix(settings) -> None:
    """Инициализирует Phoenix TracerProvider + OpenAI Instrumentor.

    Идемпотентен: повторный вызов — no-op.
    При ошибке (нет зависимостей, нет Phoenix) — логирует и продолжает.
    """
    global _tracer_provider

    if not settings.phoenix.enabled:
        log.debug("Phoenix disabled, skipping setup")
        return

    if _tracer_provider is not None:
        return  # already initialized

    try:
        from phoenix.otel import register
        from openinference.instrumentation.openai import OpenAIInstrumentor

        headers = (
            {"api_key": settings.phoenix.api_key}
            if settings.phoenix.api_key
            else None
        )

        _tracer_provider = register(
            project_name=settings.phoenix.project_name,
            endpoint=settings.phoenix.otlp_endpoint,
            protocol="grpc",
            headers=headers,
        )

        # Auto-instrument openai SDK (used by instructor and LLMClient)
        OpenAIInstrumentor().instrument(tracer_provider=_tracer_provider)

        log.info(
            "Phoenix initialized",
            endpoint=settings.phoenix.otlp_endpoint,
            project=settings.phoenix.project_name,
        )
    except ImportError:
        log.warning(
            "Phoenix dependencies not installed, tracing disabled. "
            "Install with: pip install arize-phoenix arize-phoenix-otel "
            "openinference-instrumentation-openai"
        )
    except Exception as e:
        log.warning("Phoenix setup failed, tracing disabled", error=str(e))


def get_tracer(name: str = "obratka"):
    """Возвращает OpenTelemetry tracer. Если OTel не установлен — возвращает no-op."""
    try:
        from opentelemetry import trace
        return trace.get_tracer(name)
    except ImportError:
        return None


def get_current_trace_id() -> str | None:
    """Получить trace_id текущего спана (для loguru контекста)."""
    try:
        from opentelemetry import trace
        span = trace.get_current_span()
        ctx = span.get_span_context()
        if ctx and ctx.trace_id:
            return format(ctx.trace_id, "032x")
    except (ImportError, Exception):
        pass
    return None
