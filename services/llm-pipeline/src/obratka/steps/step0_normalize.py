"""Шаг 0 — нормализация текста. Алгоритмически, без LLM. См. tasks/02_step0_normalization.md."""

from __future__ import annotations

import random
import re
from datetime import datetime
from typing import TYPE_CHECKING

from obratka.llm.schemas import NormalizedReview, RawReview
from obratka.logging_setup import get_logger
from obratka.utils.hashing import text_hash
from obratka.utils.lang import detect_language

if TYPE_CHECKING:
    from obratka.report.artifacts import ArtifactCollector

log = get_logger(__name__)

HTML_TAG = re.compile(r"<[^>]+>")
URL = re.compile(r"https?://\S+|www\.\S+|t\.me/\S+|wa\.me/\S+")
WHITESPACE = re.compile(r"\s+")
EMOJI = re.compile(
    "["
    "\U0001f600-\U0001f64f"  # smileys
    "\U0001f300-\U0001f5ff"  # symbols & pictographs
    "\U0001f680-\U0001f6ff"  # transport
    "\U0001f1e0-\U0001f1ff"  # regional indicators (флаги)
    "\U0001f900-\U0001f9ff"  # supplemental symbols
    "\U0001fa00-\U0001faff"  # symbols extended
    "\U00002300-\U000023ff"  # misc technical
    "\U00002600-\U000026ff"  # misc symbols
    "\U00002700-\U000027bf"  # dingbats
    "\U00002b00-\U00002bff"  # arrows и ⭐
    "]+",
    flags=re.UNICODE,
)

_MIN_LEN_NON_EMPTY = 5


def _clean_text(text: str) -> str:
    s = HTML_TAG.sub(" ", text)
    s = URL.sub(" ", s)
    s = EMOJI.sub(" ", s)
    s = s.lower()
    s = WHITESPACE.sub(" ", s).strip()
    return s


def normalize_review(raw: RawReview) -> NormalizedReview:
    cleaned = _clean_text(raw.text)
    lang, lang_conf = detect_language(cleaned)
    is_empty = len(cleaned) < _MIN_LEN_NON_EMPTY

    return NormalizedReview(
        review_id=raw.review_id,
        author_id=raw.author_id,
        text_raw=raw.text,
        text_normalized=cleaned,
        text_hash=text_hash(cleaned),
        lang=lang,
        lang_confidence=lang_conf,
        stars=raw.stars,
        date=raw.date,
        source=raw.source,
        is_empty=is_empty,
    )


def normalize_batch(
    raws: list[RawReview],
    *,
    collector: "ArtifactCollector | None" = None,
) -> list[NormalizedReview]:
    out: list[NormalizedReview] = []
    log_step = log.bind(step="step0")
    log_step.info("Step 0 start", input_count=len(raws))
    started_at = datetime.now()

    for i, raw in enumerate(raws, start=1):
        n = normalize_review(raw)
        if n.is_empty:
            log_step.bind(review_id=n.review_id).warning(
                "Empty after normalize", text_raw_len=len(raw.text)
            )
        elif n.lang != "ru":
            log_step.bind(review_id=n.review_id).debug(
                "Non-RU detected",
                lang=n.lang,
                lang_confidence=n.lang_confidence,
            )
        out.append(n)
        if i % 1000 == 0:
            log_step.info("Normalize progress", processed=i, total=len(raws))

    empty_count = sum(1 for n in out if n.is_empty)
    log_step.info(
        "Step 0 done",
        input_count=len(raws),
        output_count=len(out),
        empty=empty_count,
        non_ru=sum(1 for n in out if n.lang not in ("ru", "unknown")),
    )

    if collector is not None:
        from obratka.report.artifacts import (
            NormalizationSample,
            Step0Artifact,
            make_stage_stats,
        )

        lang_dist: dict[str, int] = {}
        for n in out:
            lang_dist[n.lang] = lang_dist.get(n.lang, 0) + 1

        non_empty = [n for n in out if not n.is_empty]
        sample_n = min(collector.max_samples, 10, len(non_empty))
        chosen = random.sample(non_empty, sample_n) if sample_n > 0 else []
        samples = [
            NormalizationSample(
                review_id=n.review_id,
                text_raw=n.text_raw[:500],
                text_normalized=n.text_normalized[:500],
                lang=n.lang,
            )
            for n in chosen
        ]
        collector.record_step0(
            Step0Artifact(
                stats=make_stage_stats("step0", started_at),
                input_count=len(raws),
                empty_after_norm_count=empty_count,
                lang_distribution=lang_dist,
                samples=samples,
            )
        )

    return out
