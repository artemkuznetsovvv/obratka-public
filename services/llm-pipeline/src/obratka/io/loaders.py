"""Загрузчики JSON в RawReview. Поддерживают разные форматы парсеров площадок.

Auto-detect:
- "flat":      [{"review_id": ..., "text": ..., "date": ..., "source": ...}, ...]
- "scraper_v1": {"source": "yandex", "external_id": "<biz>", "reviews": [
                  {"external_id": ..., "text": ..., "date": ...,
                   "author_public_id": ..., "stars": ...}, ...]}
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from obratka.llm.schemas import RawReview


def detect_format(data: Any) -> str:
    if isinstance(data, list):
        return "flat"
    if isinstance(data, dict) and "reviews" in data and isinstance(data["reviews"], list):
        return "scraper_v1"
    raise ValueError(
        "Не удалось определить формат JSON. Ожидается либо массив отзывов, "
        "либо объект с полем 'reviews' (формат парсера площадки)."
    )


def _from_flat(items: list[dict]) -> list[RawReview]:
    return [RawReview.model_validate(it) for it in items]


def _from_scraper_v1(payload: dict) -> list[RawReview]:
    """Адаптер для формата нашего парсера площадок.

    Маппинг полей:
      review_id  ← external_id
      author_id  ← author_public_id (или None)
      source     ← корневое payload["source"]
      stars/date/text — как есть
    """
    source = payload.get("source") or "unknown"
    raws: list[RawReview] = []
    for r in payload["reviews"]:
        raws.append(
            RawReview(
                review_id=r["external_id"],
                author_id=r.get("author_public_id") or None,
                text=r.get("text", ""),
                stars=r.get("stars"),
                date=r["date"],
                source=r.get("source") or source,
            )
        )
    return raws


def load_reviews(path: str | Path) -> list[RawReview]:
    """Загружает JSON файл и возвращает список RawReview."""
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    fmt = detect_format(data)
    if fmt == "flat":
        return _from_flat(data)
    if fmt == "scraper_v1":
        return _from_scraper_v1(data)
    raise ValueError(f"Unknown format: {fmt}")
