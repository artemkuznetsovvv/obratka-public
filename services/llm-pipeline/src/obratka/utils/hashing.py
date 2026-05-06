"""Стабильный хэш текста — ключ кэша по `text_normalized`."""

from __future__ import annotations

import hashlib


def text_hash(text: str) -> str:
    """sha256 hex от UTF-8 байт текста. Используется как ключ кэша."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()
