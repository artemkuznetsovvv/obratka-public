"""Loguru → Seq sink (CLEF поверх HTTP).

Seq принимает структурированные события в формате CLEF (Compact Log Event Format):
по одному JSON-объекту на строку на эндпоинт `/ingest/clef`. Serilog (.NET-сервисы
стэка) шлёт туда же — так Python-логи окажутся в том же Seq и будут фильтроваться
по тем же свойствам.

Устойчивость: недоступность Seq не должна ронять приложение. POST обёрнут в
try/except, а сам sink навешивается с `enqueue=True` (запись идёт в фоновом потоке
loguru, не блокируя asyncio event loop). При первой ошибке пишем одно
предупреждение в stderr, дальше — тихо, чтобы не спамить при долгом простое Seq.
"""

from __future__ import annotations

import json
import sys
import traceback
import urllib.request
from collections.abc import Callable
from datetime import timezone
from typing import Any

# loguru level name -> CLEF/Seq level
_LEVEL_MAP = {
    "TRACE": "Verbose",
    "DEBUG": "Debug",
    "INFO": "Information",
    "SUCCESS": "Information",
    "WARNING": "Warning",
    "ERROR": "Error",
    "CRITICAL": "Fatal",
}

_SECRET_HINTS = ("api_key", "authorization", "password", "token", "secret")

# Плейсхолдеры из _DEFAULT_EXTRAS (logging_setup) — не засоряем ими события Seq.
_PLACEHOLDER = "-"


def _normalize_endpoint(url: str) -> str:
    base = url.rstrip("/")
    if base.endswith("/ingest/clef"):
        return base
    return f"{base}/ingest/clef"


def _clef_event(record: dict[str, Any], static_props: dict[str, Any]) -> dict[str, Any]:
    """Преобразует loguru-record в CLEF-событие."""
    event: dict[str, Any] = dict(static_props)
    event["@t"] = record["time"].astimezone(timezone.utc).isoformat()
    event["@l"] = _LEVEL_MAP.get(record["level"].name, record["level"].name)
    # record["message"] — «сырое» сообщение без интерполяции (структура живёт в
    # extra), поэтому в Seq события естественно группируются по тексту.
    event["@m"] = record["message"]

    # Источник — как свойства (удобно фильтровать по logger/function).
    event["logger"] = record["name"]
    event["function"] = record["function"]
    event["line"] = record["line"]

    exc = record["exception"]
    if exc is not None:
        try:
            event["@x"] = "".join(
                traceback.format_exception(exc.type, exc.value, exc.traceback)
            )
        except Exception:
            event["@x"] = f"{getattr(exc.type, '__name__', 'Error')}: {exc.value}"

    # extra -> свойства. Фильтр _redact_secrets в logging_setup уже маскирует
    # секреты в общем record, но дублируем тут как defense-in-depth (sink может
    # быть навешен и без него).
    for key, value in record["extra"].items():
        if value == _PLACEHOLDER:
            continue
        if any(hint in key.lower() for hint in _SECRET_HINTS):
            value = "***REDACTED***"
        # @-префикс зарезервирован CLEF — экранируем двойным @@ по спецификации.
        event[f"@@{key}" if key.startswith("@") else key] = value

    return event


def make_seq_sink(
    url: str,
    *,
    api_key: str | None = None,
    timeout: float = 5.0,
    static_props: dict[str, Any] | None = None,
) -> Callable[[Any], None]:
    """Возвращает loguru-sink, отправляющий каждое событие в Seq (CLEF).

    Навешивать с `enqueue=True`, чтобы HTTP не блокировал event loop.
    """
    endpoint = _normalize_endpoint(url)
    headers = {"Content-Type": "application/vnd.serilog.clef"}
    if api_key:
        headers["X-Seq-ApiKey"] = api_key
    props = static_props or {}
    state = {"warned": False}

    def sink(message: Any) -> None:
        try:
            event = _clef_event(message.record, props)
            body = (json.dumps(event, ensure_ascii=False) + "\n").encode("utf-8")
            req = urllib.request.Request(
                endpoint, data=body, method="POST", headers=headers
            )
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                resp.read()
        except Exception as e:  # никогда не роняем приложение из-за логирования
            if not state["warned"]:
                print(
                    f"[seq_sink] failed to ship logs to Seq ({endpoint}): "
                    f"{type(e).__name__}: {e}. Suppressing further warnings.",
                    file=sys.stderr,
                )
                state["warned"] = True

    return sink
