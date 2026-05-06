"""Loguru-конфигурация: 4 sink'а (консоль, текстовый файл, JSONL, errors).

См. tasks/00_logging.md.
"""

from __future__ import annotations

import sys
from pathlib import Path

from loguru import logger

_DEFAULT_EXTRAS = {
    "step": "-",
    "run_id": "-",
    "run_id_short": "-",
    "batch_id": "-",
    "trace_id": "-",
}

_SECRET_HINTS = ("api_key", "authorization", "password", "token", "secret")


def _redact_secrets(record) -> bool:
    """Маскирует значения секретных полей в extra перед записью."""
    extra = record["extra"]
    for k in list(extra.keys()):
        if any(s in k.lower() for s in _SECRET_HINTS):
            extra[k] = "***REDACTED***"
    return True


def setup_logging(level: str = "INFO", logs_dir: str = "logs") -> None:
    """Идемпотентен: повторный вызов снимает все предыдущие sink'и и пересоздаёт их."""
    Path(logs_dir).mkdir(parents=True, exist_ok=True)

    logger.remove()
    logger.configure(extra=_DEFAULT_EXTRAS)

    console_fmt = (
        "<green>{time:YYYY-MM-DD HH:mm:ss.SSS}</green> | "
        "<level>{level: <5}</level> | "
        "<cyan>{extra[step]:<10}</cyan> | "
        "<dim>run={extra[run_id_short]} batch={extra[batch_id]}</dim> | "
        "<level>{message}</level>"
    )
    text_fmt = (
        "{time:YYYY-MM-DDTHH:mm:ss.SSSZ} | {level: <5} | "
        "{name}:{function}:{line} | "
        "run_id={extra[run_id]} step={extra[step]} batch_id={extra[batch_id]} | "
        "{message}"
    )

    logger.add(
        sys.stderr,
        level=level,
        format=console_fmt,
        colorize=True,
        filter=_redact_secrets,
    )
    logger.add(
        f"{logs_dir}/obratka.log",
        level="DEBUG",
        format=text_fmt,
        rotation="50 MB",
        retention="14 days",
        compression="zip",
        filter=_redact_secrets,
    )
    # JSONL sink убран — его роль теперь у Phoenix (OpenTelemetry трейсы).
    logger.add(
        f"{logs_dir}/errors.log",
        level="ERROR",
        format=text_fmt,
        rotation="10 MB",
        retention="90 days",
        backtrace=True,
        diagnose=True,
        filter=_redact_secrets,
    )


def get_logger(name: str | None = None):
    """Возвращает логгер с привязкой component=<name> для удобной фильтрации."""
    return logger.bind(component=name or "obratka")
