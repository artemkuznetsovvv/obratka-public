"""REST status-endpoint для PG.

PG поллит `/status/{analysis_job_id}` чтобы показать прогресс на UI.
Также используется как docker healthcheck (`/health/live`).
Авторизации нет — сервис живёт во внутренней docker-сети `parser-internal`.

Опционально, в dev-окружении подключается QA-роутер `POST /qa/analyze`
для прогона пайплайна по локальному JSON без RabbitMQ + S3.
"""

from __future__ import annotations

from pathlib import Path

from fastapi import FastAPI
from fastapi.responses import JSONResponse

from obratka.web.state import JobStateStore


def make_app(
    store: JobStateStore,
    *,
    qa_enabled: bool = False,
    qa_output_dir: Path | None = None,
) -> FastAPI:
    app = FastAPI(
        title="Obratka LLM Status API",
        version="2.0",
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )

    @app.get("/health/live")
    async def health_live() -> dict[str, str]:
        return {"status": "alive"}

    @app.get("/status/{analysis_job_id}")
    async def get_status(analysis_job_id: str):
        state = store.get(analysis_job_id)
        if state is None:
            # PG ожидает 404 + структурированный ответ — см. QUICKSTART §5.
            return JSONResponse(
                status_code=404,
                content={
                    "analysis_job_id": analysis_job_id,
                    "status":          "unknown",
                },
            )
        return state

    if qa_enabled:
        # Импорт внутри ветки — чтобы прод не тащил FastAPI body-parsing зависимости
        # для эндпоинта, который не зарегистрирован.
        from obratka.web.qa import make_qa_router

        out_dir = qa_output_dir or Path("qa_outputs")
        app.include_router(make_qa_router(store=store, output_dir=out_dir))

    return app
