"""Ретраи и backoff для LLM-вызовов.

Сетевые ошибки и 5xx → экспоненциальный backoff с джиттером (1s → 2s → 4s).
429 → отдельная обработка через retry_after (см. LLMClient).
Ошибки парсинга JSON ретраит сам instructor — эта обёртка их не трогает.
"""

from __future__ import annotations

from tenacity import (
    AsyncRetrying,
    retry_if_exception_type,
    stop_after_attempt,
    wait_exponential_jitter,
)

# Ленивые импорты ошибок openai — иначе тесты падают, если SDK не установлен.
try:
    from openai import (
        APIConnectionError,
        APITimeoutError,
        InternalServerError,
        RateLimitError,
    )

    NETWORK_ERRORS: tuple[type[Exception], ...] = (
        APIConnectionError,
        APITimeoutError,
        InternalServerError,
    )
    RATE_LIMIT_ERRORS: tuple[type[Exception], ...] = (RateLimitError,)
except Exception:  # pragma: no cover — окружение без openai
    NETWORK_ERRORS = ()
    RATE_LIMIT_ERRORS = ()


def make_retrying(max_attempts: int = 3) -> AsyncRetrying:
    """Универсальный AsyncRetrying для сетевых ошибок + 429."""
    return AsyncRetrying(
        stop=stop_after_attempt(max_attempts),
        wait=wait_exponential_jitter(initial=1.0, max=8.0),
        retry=retry_if_exception_type(NETWORK_ERRORS + RATE_LIMIT_ERRORS),
        reraise=True,
    )
