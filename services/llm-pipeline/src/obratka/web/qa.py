"""QA-ручка для локального тестирования пайплайна без RabbitMQ + S3.

Принимает на вход JSON ровно того же формата, что PG кладёт в S3
(`input.json`), запускает тот же путь, что AMQP-worker, и пишет два
output-файла на локальный диск. Это позволяет быстро прогонять контракт
по реальным сэмплам без поднятия PG/MinIO.

⚠️ Только для dev. Регистрируется в FastAPI-приложении только если
`OBRATKA_QA_ENABLED=true`, иначе ручки физически нет (404 без следов в OpenAPI).
"""

from __future__ import annotations

import asyncio
import json
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal

from fastapi import APIRouter, Body, HTTPException, Query
from loguru import logger

from obratka.analyze_reviews import (
    InputError,
    analyze_payload,
    analyze_payload_llm,
)
from obratka.web.contract import SCHEMA_VERSION, build_outputs
from obratka.web.state import JobStateStore


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _resolve_job_id(payload: dict[str, Any]) -> str:
    """analysis_job_id из payload или сгенерированный UUID для ad-hoc запуска."""
    raw = payload.get("analysis_job_id")
    if raw:
        return str(raw)
    return f"qa-{uuid.uuid4()}"


def make_qa_router(*, store: JobStateStore, output_dir: Path) -> APIRouter:
    output_dir.mkdir(parents=True, exist_ok=True)
    router = APIRouter(prefix="/qa", tags=["qa"])

    @router.post(
        "/analyze",
        summary="Run pipeline on inline input.json (dev only)",
    )
    async def analyze(
        payload: dict[str, Any] = Body(..., description="Содержимое input.json целиком."),
        engine: Literal["llm", "local"] = Query(
            "llm",
            description=(
                "llm — полный пайплайн через OpenRouter (требует OPENROUTER_API_KEY); "
                "local — детерминистический fallback из analyze_payload (без LLM)."
            ),
        ),
    ) -> dict[str, Any]:
        if not isinstance(payload, dict):
            raise HTTPException(status_code=400, detail="JSON body must be an object")

        job_id = _resolve_job_id(payload)
        # Если запрос пришёл без analysis_job_id — подставим сгенерированный, чтобы
        # дальнейшие проверки матчились (build_outputs использует это поле).
        payload.setdefault("analysis_job_id", job_id)

        job_dir = output_dir / job_id
        job_dir.mkdir(parents=True, exist_ok=True)

        with logger.contextualize(analysis_job_id=job_id, qa_engine=engine):
            logger.info("QA analyze request received")
            store.start(job_id, started_at=_now_iso())
            store.update(job_id, stage="inferring", progress=0.1)

            t0 = time.monotonic()
            try:
                if engine == "llm":
                    core_aspects, core_recommendations = await analyze_payload_llm(payload)
                else:
                    # analyze_payload — sync детерминистический; уносим в executor,
                    # чтобы не держать event loop при больших inputs.
                    core_aspects, core_recommendations = await asyncio.to_thread(
                        analyze_payload, payload
                    )
            except InputError as e:
                store.update(job_id, status="failed", error=str(e), finished_at=_now_iso())
                logger.warning(f"QA input validation failed: {e}")
                raise HTTPException(status_code=400, detail=f"Invalid input: {e}") from e
            except Exception as e:
                err = f"{type(e).__name__}: {e}"
                store.update(job_id, status="failed", error=err, finished_at=_now_iso())
                logger.exception(f"QA analyze failed: {err}")
                raise HTTPException(status_code=500, detail=err) from e

            store.update(job_id, stage="uploading_output", progress=0.95)

            output_reviews, output_summary = build_outputs(
                analysis_job_id=job_id,
                input_reviews=payload.get("reviews") or [],
                core_aspects=core_aspects,
                core_recommendations=core_recommendations,
            )

            reviews_path = job_dir / "output_reviews.json"
            summary_path = job_dir / "output_summary.json"
            reviews_path.write_text(
                json.dumps(output_reviews, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            summary_path.write_text(
                json.dumps(output_summary, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )

            # Дополнительно сохраняем ровно то тело, которое мы бы запостили
            # в очередь llm.results — удобно для отладки контракта.
            llm_result_message = {
                "analysis_job_id":    job_id,
                "status":             "finished",
                "result_reviews_url": f"file://{reviews_path.resolve().as_posix()}",
                "result_summary_url": f"file://{summary_path.resolve().as_posix()}",
                "schema_version":     SCHEMA_VERSION,
            }
            (job_dir / "llm_result_message.json").write_text(
                json.dumps(llm_result_message, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )

            elapsed = round(time.monotonic() - t0, 2)
            store.update(
                job_id,
                status="finished",
                stage="done",
                progress=1.0,
                finished_at=_now_iso(),
                error=None,
            )
            logger.info(f"QA analyze finished in {elapsed}s, output dir: {job_dir}")

            return {
                "analysis_job_id":     job_id,
                "status":              "finished",
                "engine":              engine,
                "output_dir":          str(job_dir.resolve()),
                "output_reviews_path": str(reviews_path.resolve()),
                "output_summary_path": str(summary_path.resolve()),
                "schema_version":      SCHEMA_VERSION,
                "elapsed_seconds":     elapsed,
                "stats": {
                    "total_reviews":  len(payload.get("reviews") or []),
                    "aspects_total":  sum(len(r["aspects"]) for r in output_reviews["reviews"]),
                },
            }

    return router
