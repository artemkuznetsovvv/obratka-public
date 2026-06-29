"""Хелперы для ручных спанов на уровне шагов и батчей.

Если OpenTelemetry не установлен — контекст-менеджеры работают как no-op.
"""

from __future__ import annotations

from contextlib import asynccontextmanager
from typing import Any


class _NoOpSpan:
    """Заглушка, если OTel недоступен."""

    def set_attribute(self, key: str, value: Any) -> None:
        pass

    def set_status(self, *args, **kwargs) -> None:
        pass

    def record_exception(self, exc: Exception) -> None:
        pass


def _get_tracer():
    try:
        from opentelemetry import trace
        return trace.get_tracer("obratka")
    except ImportError:
        return None


@asynccontextmanager
async def step_span(step_name: str, **attrs):
    """Спан вокруг шага пайплайна. No-op если OTel не установлен."""
    tracer = _get_tracer()
    if tracer is None:
        yield _NoOpSpan()
        return

    from opentelemetry.trace import Status, StatusCode

    with tracer.start_as_current_span(f"step.{step_name}") as span:
        for k, v in attrs.items():
            span.set_attribute(k, _safe_attr(v))
        try:
            yield span
        except Exception as e:
            span.set_status(Status(StatusCode.ERROR, str(e)))
            span.record_exception(e)
            raise


@asynccontextmanager
async def batch_span(step_name: str, batch_id: str, batch_size: int):
    """Спан вокруг одного батча. No-op если OTel не установлен."""
    tracer = _get_tracer()
    if tracer is None:
        yield _NoOpSpan()
        return

    with tracer.start_as_current_span(f"batch.{step_name}") as span:
        span.set_attribute("batch.id", batch_id)
        span.set_attribute("batch.size", batch_size)
        try:
            yield span
        except Exception as e:
            from opentelemetry.trace import Status, StatusCode
            span.set_status(Status(StatusCode.ERROR, str(e)))
            span.record_exception(e)
            raise


def _safe_attr(value: Any) -> Any:
    """OTel attributes must be str/int/float/bool or list of those."""
    if isinstance(value, (str, int, float, bool)):
        return value
    return str(value)
