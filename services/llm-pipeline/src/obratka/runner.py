from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Callable
from typing import Generic, TypeVar

T = TypeVar("T")
R = TypeVar("R")

class BatchRunner(Generic[T, R]):
    def __init__(self, max_concurrency: int = 8):
        self._sem = asyncio.Semaphore(max_concurrency)
        self.max_concurrency = max_concurrency

    async def run_many(
        self,
        items: list[T],
        worker: Callable[[T], Awaitable[R]],
    ) -> list[R]:
        if not items:
            return []

        async def guarded(item: T) -> R:
            async with self._sem:
                return await worker(item)

        return await asyncio.gather(*(guarded(i) for i in items))
