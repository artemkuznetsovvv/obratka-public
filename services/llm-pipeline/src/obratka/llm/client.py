"""Async-клиент OpenRouter через instructor + OpenAI SDK.

См. tasks/01_async_orchestrator.md.
"""

from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass
from typing import TYPE_CHECKING, Type, TypeVar

from pydantic import BaseModel

from obratka.config import ModelConfig, ModelPricing
from obratka.logging_setup import get_logger

if TYPE_CHECKING:
    from openai import AsyncOpenAI

T = TypeVar("T", bound=BaseModel)
log = get_logger(__name__)


@dataclass
class UsageInfo:
    prompt_tokens: int
    completion_tokens: int
    cost_usd: float
    model: str
    latency_ms: float


def calc_cost(prompt_tokens: int, completion_tokens: int, pricing: ModelPricing) -> float:
    return (
        prompt_tokens * pricing.input_per_m / 1_000_000
        + completion_tokens * pricing.output_per_m / 1_000_000
    )


class LLMClient:
    """Единая точка входа для всех LLM-вызовов в пайплайне.

    - Все шаги обращаются к OpenRouter только через этот клиент.
    - Если задан response_model — используется instructor для structured output
      с авто-ретраем на невалидный JSON.
    - Расход $ агрегируется в self.total_cost (под asyncio.Lock).
    """

    def __init__(
        self,
        api_key: str,
        models: dict[str, ModelConfig],
        base_url: str = "https://openrouter.ai/api/v1",
        default_headers: dict | None = None,
    ):
        if not api_key:
            raise ValueError("OPENROUTER_API_KEY is required")
        self.api_key = api_key
        self.base_url = base_url
        self.models = models
        self.default_headers = default_headers or {}

        self._client: "AsyncOpenAI | None" = None
        self._instructor_client = None
        self._total_cost = 0.0
        self._cost_lock = asyncio.Lock()

    def _ensure_clients(self) -> None:
        if self._client is not None:
            return
        from openai import AsyncOpenAI

        self._client = AsyncOpenAI(
            api_key=self.api_key,
            base_url=self.base_url,
            default_headers=self.default_headers,
        )
        try:
            import instructor

            self._instructor_client = instructor.from_openai(self._client)
        except Exception:
            self._instructor_client = None

    async def complete(
        self,
        *,
        model: str,
        messages: list[dict],
        response_model: Type[T] | None = None,
        max_retries: int = 3,
        temperature: float = 0.0,
        timeout: float = 60.0,
        request_id: str | None = None,
    ) -> tuple[T | str, UsageInfo]:
        if model not in self.models:
            raise KeyError(f"Unknown model alias: {model}")

        self._ensure_clients()
        return await self._complete_cfg(
            self.models[model],
            messages=messages,
            response_model=response_model,
            max_retries=max_retries,
            temperature=temperature,
            timeout=timeout,
            request_id=request_id,
        )

    async def _complete_cfg(
        self,
        cfg: ModelConfig,
        *,
        messages: list[dict],
        response_model: Type[T] | None,
        max_retries: int,
        temperature: float,
        timeout: float,
        request_id: str | None,
    ) -> tuple[T | str, UsageInfo]:
        """Вызов конкретной модели с переходом на cfg.fallback при ошибке.

        Ловим широко (Exception): instructor оборачивает ошибки провайдера
        (404 «No endpoints found», rate-limit и пр.) в свои исключения, поэтому
        узкая фильтрация по типам openai пропустила бы их. CancelledError —
        BaseException и сюда не попадает.
        """
        try:
            return await self._call_once(
                cfg,
                messages=messages,
                response_model=response_model,
                max_retries=max_retries,
                temperature=temperature,
                timeout=timeout,
                request_id=request_id,
            )
        except Exception as e:
            if cfg.fallback is None:
                raise
            log.bind(request_id=request_id or "-").warning(
                "LLM primary model failed, falling back to backup",
                primary=cfg.openrouter_id,
                fallback=cfg.fallback.openrouter_id,
                error=f"{type(e).__name__}: {e}",
            )
            return await self._complete_cfg(
                cfg.fallback,
                messages=messages,
                response_model=response_model,
                max_retries=max_retries,
                temperature=temperature,
                timeout=timeout,
                request_id=request_id,
            )

    async def _call_once(
        self,
        cfg: ModelConfig,
        *,
        messages: list[dict],
        response_model: Type[T] | None,
        max_retries: int,
        temperature: float,
        timeout: float,
        request_id: str | None,
    ) -> tuple[T | str, UsageInfo]:
        rlog = log.bind(model=cfg.openrouter_id, request_id=request_id or "-")

        rlog.debug(
            "LLM request",
            prompt_chars=sum(len(m.get("content", "")) for m in messages),
        )
        start = time.perf_counter()

        if response_model is not None:
            if self._instructor_client is None:
                raise RuntimeError(
                    "instructor is required for structured output but not installed"
                )
            content, raw = await self._instructor_client.chat.completions.create_with_completion(
                model=cfg.openrouter_id,
                messages=messages,
                response_model=response_model,
                max_retries=max_retries,
                temperature=temperature,
                timeout=timeout,
            )
            usage = raw.usage
        else:
            raw = await self._client.chat.completions.create(  # type: ignore[union-attr]
                model=cfg.openrouter_id,
                messages=messages,
                temperature=temperature,
                timeout=timeout,
            )
            usage = raw.usage
            content = raw.choices[0].message.content

        latency_ms = (time.perf_counter() - start) * 1000.0
        cost = calc_cost(usage.prompt_tokens, usage.completion_tokens, cfg.pricing)
        async with self._cost_lock:
            self._total_cost += cost

        info = UsageInfo(
            prompt_tokens=usage.prompt_tokens,
            completion_tokens=usage.completion_tokens,
            cost_usd=cost,
            model=cfg.openrouter_id,
            latency_ms=latency_ms,
        )
        rlog.debug(
            "LLM response",
            prompt_tokens=info.prompt_tokens,
            completion_tokens=info.completion_tokens,
            cost_usd=round(info.cost_usd, 6),
            latency_ms=round(info.latency_ms, 1),
        )
        return content, info

    @property
    def total_cost(self) -> float:
        return self._total_cost
