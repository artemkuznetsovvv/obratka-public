"""Обёртка над langdetect: детерминированная детекция языка с фолбэками."""

from __future__ import annotations

import langdetect
from langdetect import DetectorFactory, detect_langs

# Детерминизм — без этого один и тот же текст может детектиться по-разному.
DetectorFactory.seed = 0

_MIN_LEN_FOR_DETECT = 10


def detect_language(text: str) -> tuple[str, float]:
    """Возвращает (lang, confidence).

    - Тексты короче _MIN_LEN_FOR_DETECT → ('ru', 0.0): на коротком langdetect ненадёжен.
    - LangDetectException → ('unknown', 0.0).
    """
    if len(text.strip()) < _MIN_LEN_FOR_DETECT:
        return ("ru", 0.0)
    try:
        candidates = detect_langs(text)
        if not candidates:
            return ("unknown", 0.0)
        top = candidates[0]
        return (top.lang, float(top.prob))
    except langdetect.LangDetectException:
        return ("unknown", 0.0)
