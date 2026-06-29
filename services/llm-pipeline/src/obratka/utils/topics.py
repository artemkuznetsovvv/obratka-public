"""Topic normalization helpers for LLM-produced review aspects."""

from __future__ import annotations

from obratka.llm.schemas import ReviewAnalysis


BASE_TOPICS = [
    "еда/напитки",
    "персонал",
    "скорость обслуживания",
    "чистота",
    "цена/качество",
    "атмосфера",
    "локация/парковка",
    "запись/коммуникация",
]


def normalize_topic(topic: str, fragment: str = "") -> str:
    """Map common freeform/invalid LLM topics to the canonical topic set."""
    raw = (topic or "").strip()
    t = raw.lower().replace("_", " ").replace("-", " ")
    f = (fragment or "").lower()
    haystack = f"{t} {f}"

    if raw in BASE_TOPICS:
        return raw

    if "кофе" in haystack or "напит" in haystack or "капуч" in haystack:
        return "еда/напитки"
    if "еда" in haystack or "блюд" in haystack or "десерт" in haystack:
        return "еда/напитки"
    if "бариста" in haystack or "сотрудник" in haystack or "персонал" in haystack:
        return "персонал"
    if "очеред" in haystack or "ждал" in haystack or "долго" in haystack:
        return "скорость обслуживания"
    if "гряз" in haystack or "пыль" in haystack or "посуда" in haystack:
        return "чистота"
    if "цен" in haystack or "дорог" in haystack or "обоснован" in haystack:
        return "цена/качество"
    if "парков" in haystack or "локац" in haystack or "местополож" in haystack:
        return "локация/парковка"
    if "интерьер" in haystack or "атмосфер" in haystack or "тесно" in haystack:
        return "атмосфера"
    if "стол" in haystack or "пинг" in haystack or "шум" in haystack:
        return "атмосфера"
    if "интернет" in haystack or "wi fi" in haystack or "wifi" in haystack:
        return "атмосфера"
    if "сказал" in haystack or "администратор" in haystack or "нельзя" in haystack:
        return "запись/коммуникация"
    if "предупрежд" in haystack or "коммуникац" in haystack or "фильтр" in haystack:
        return "запись/коммуникация"

    if t.startswith("is freeform") or t in {"is_freeform", "is freeform=true", "is freeform true"}:
        return "запись/коммуникация"

    return raw


def normalize_analysis_topics(analyses: list[ReviewAnalysis]) -> list[ReviewAnalysis]:
    for analysis in analyses:
        for aspect in analysis.aspects:
            normalized = normalize_topic(aspect.topic, aspect.fragment)
            aspect.topic = normalized
            aspect.is_freeform = normalized not in BASE_TOPICS
    return analyses
