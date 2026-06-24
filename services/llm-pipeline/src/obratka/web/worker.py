"""AMQP worker — точка входа `obratka-worker`.

Один процесс, один event loop:
- `amqp_loop()` слушает `llm.requests`, скачивает input.json, вызывает
  `analyze_payload_llm`, кладёт два output-файла в S3, публикует ответ в
  `llm.results` (или в очередь из `callback_queue`).
- `uvicorn` поднимает FastAPI на `LLM_HTTP_PORT` для PG-status-поллинга.

Концепции:
- prefetch_count = LLM_MAX_PARALLEL_JOBS (по умолчанию 3) — столько jobs
  обрабатывается одновременно. aio-pika запускает обработчик каждого сообщения
  отдельной задачей, поэтому prefetch и задаёт степень параллелизма между jobs.
  Каждый job внутри параллелит батчи своим пулом (pipeline.max_concurrency),
  так что пиковая нагрузка на OpenRouter ~ parallel_jobs × max_concurrency.
- requeue=False при ошибке — PG имеет свою replay-ручку, локальный requeue
  заваливал бы DLQ при стабильно-плохом input.
- Идемпотентность — повторный приход того же `analysis_job_id` перезатирает
  S3 output-ы и состояние в SQLite.
"""

from __future__ import annotations

import asyncio
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import uvicorn
from aio_pika import DeliveryMode, IncomingMessage, Message, connect_robust
from aio_pika.abc import AbstractRobustChannel, AbstractRobustConnection
from loguru import logger

from obratka.analyze_reviews import analyze_payload_llm, ensure_runtime_initialized
from obratka.config import get_settings
from obratka.web.contract import SCHEMA_VERSION, build_outputs
from obratka.web.s3 import S3Client
from obratka.web.state import JobStateStore
from obratka.web.status_api import make_app


REQUESTS_QUEUE = "llm.requests"
DEFAULT_RESULTS_QUEUE = "llm.results"

# Прогресс — минимальный, монотонно растущий. Тонкая гранулярность внутри
# `inferring` потребовала бы коллбэков из ядра — оставим на будущее.
STAGE_PROGRESS: dict[str, float] = {
    "received":          0.0,
    "downloading_input": 0.05,
    "inferring":         0.10,
    "uploading_output":  0.95,
    "done":              1.0,
}


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


class Worker:
    def __init__(
        self,
        *,
        store: JobStateStore,
        s3: S3Client,
        rabbit_url: str,
        results_queue_default: str = DEFAULT_RESULTS_QUEUE,
        max_parallel_jobs: int = 3,
    ) -> None:
        self._store = store
        self._s3 = s3
        self._rabbit_url = rabbit_url
        self._results_queue_default = results_queue_default
        self._max_parallel_jobs = max(1, max_parallel_jobs)

        self._connection: AbstractRobustConnection | None = None
        self._publish_channel: AbstractRobustChannel | None = None
        # Несколько jobs могут завершиться одновременно и публиковать ответ
        # в один и тот же канал — сериализуем публикации, чтобы не зависеть от
        # тонкостей обработки publisher-confirms при конкурентных publish.
        self._publish_lock = asyncio.Lock()

    # --- public --------------------------------------------------------------

    async def amqp_loop(self) -> None:
        self._connection = await connect_robust(self._rabbit_url)
        # Отдельные каналы на consume и publish — стандартная практика, чтобы
        # publisher confirms / flow control не мешали consume и наоборот.
        self._publish_channel = await self._connection.channel()
        consume_channel = await self._connection.channel()
        # prefetch = сколько jobs обрабатываем параллельно. aio-pika исполняет
        # _on_message каждой доставки отдельной задачей, поэтому до N сообщений
        # окажутся «в полёте» одновременно.
        await consume_channel.set_qos(prefetch_count=self._max_parallel_jobs)
        queue = await consume_channel.declare_queue(REQUESTS_QUEUE, durable=True)
        logger.info(
            f"AMQP worker listening on '{REQUESTS_QUEUE}' queue "
            f"(max_parallel_jobs={self._max_parallel_jobs})"
        )
        await queue.consume(self._on_message)
        # Висим вечно — соединение `connect_robust` пере-подключается само.
        await asyncio.Future()

    # --- consume handler -----------------------------------------------------

    async def _on_message(self, message: IncomingMessage) -> None:
        # ignore_processed=True — на случай если внутренний код двойным
        # ack-нет (например ручной message.ack() + контекст-менеджер).
        async with message.process(requeue=False, ignore_processed=True):
            await self._handle_envelope(message.body)

    async def _handle_envelope(self, raw_body: bytes) -> None:
        try:
            envelope = json.loads(raw_body.decode("utf-8"))
        except json.JSONDecodeError as e:
            logger.error(f"Inbound message has invalid JSON, dropped: {e}")
            return

        # MassTransit оборачивает payload в `message`. Если PG когда-то
        # перейдёт на raw JSON — `body.get("message", body)` обеспечит совместимость.
        payload = envelope.get("message", envelope) if isinstance(envelope, dict) else {}

        job_id = payload.get("analysis_job_id") or payload.get("analysisJobId")
        input_url = payload.get("payload_url") or payload.get("payloadUrl")
        callback_queue = (
            payload.get("callback_queue")
            or payload.get("callbackQueue")
            or self._results_queue_default
        )

        if not job_id:
            logger.error(f"Inbound message missing analysis_job_id, dropped: {envelope!r}")
            return

        # CorrelationId сквозной трассировки. MassTransit кладёт его на верхний
        # уровень конверта; если нет — берём conversationId, поля payload, а в
        # крайнем случае сам job_id, чтобы свойство всегда было заполнено.
        correlation_id = (
            envelope.get("correlationId")
            or envelope.get("conversationId")
            or payload.get("correlation_id")
            or payload.get("correlationId")
            or str(job_id)
        )

        # PascalCase-имена (AnalysisJobId / CorrelationId) — чтобы в Seq свойства
        # совпадали с .NET-сервисами (Serilog) и логи коррелировались сквозь сервисы.
        with logger.contextualize(
            AnalysisJobId=str(job_id),
            CorrelationId=str(correlation_id),
        ):
            logger.info("Inbound LLM request received")
            self._store.start(str(job_id), started_at=_now_iso())

            try:
                response_body = await self._run_job(str(job_id), input_url)
            except Exception as e:
                err = f"{type(e).__name__}: {e}"
                logger.exception(f"Job failed: {err}")
                response_body = {
                    "analysis_job_id": str(job_id),
                    "status":          "failed",
                    "error":           err,
                    "schema_version":  SCHEMA_VERSION,
                }
                self._store.update(
                    str(job_id),
                    status="failed",
                    error=err,
                    finished_at=_now_iso(),
                )

            await self._publish_result(callback_queue, response_body)
            logger.info(
                f"Reply published to '{callback_queue}': status={response_body.get('status')}"
            )

    # --- core job pipeline ---------------------------------------------------

    async def _run_job(self, job_id: str, input_url: str | None) -> dict[str, Any]:
        if not input_url:
            raise ValueError("Missing payload_url in inbound message")

        # 1) Download input.json
        self._set_stage(job_id, "downloading_input")
        input_data = await self._s3.get_json(input_url)

        if not isinstance(input_data, dict):
            raise ValueError("input.json must be a JSON object")

        # job_id из брокера должен совпадать с analysis_job_id внутри input.json.
        # Иначе кто-то перепутал ссылку — лучше провалить явно, чем смешать данные.
        input_job_id = str(input_data.get("analysis_job_id", ""))
        if input_job_id != job_id:
            raise ValueError(
                f"input.json analysis_job_id mismatch: "
                f"expected {job_id!r}, got {input_job_id!r}"
            )

        reviews = input_data.get("reviews") or []

        # 2) LLM inference (ядро валидирует payload и параллелит батчи внутри)
        self._set_stage(job_id, "inferring")
        core_aspects, core_recommendations = await analyze_payload_llm(input_data)
        # Промежуточная отметка — между уходом ядра и началом upload.
        self._store.update(job_id, progress=0.85)

        # 3) Build & upload output files
        self._set_stage(job_id, "uploading_output")
        output_reviews, output_summary = build_outputs(
            analysis_job_id=job_id,
            input_reviews=reviews,
            core_aspects=core_aspects,
            core_recommendations=core_recommendations,
        )
        reviews_url = await self._s3.put_json(f"{job_id}/output_reviews.json", output_reviews)
        summary_url = await self._s3.put_json(f"{job_id}/output_summary.json", output_summary)

        # 4) Done
        self._set_stage(job_id, "done")
        self._store.update(
            job_id,
            status="finished",
            progress=1.0,
            finished_at=_now_iso(),
            error=None,
        )

        return {
            "analysis_job_id":    job_id,
            "status":             "finished",
            "result_reviews_url": reviews_url,
            "result_summary_url": summary_url,
            "schema_version":     SCHEMA_VERSION,
        }

    # --- helpers -------------------------------------------------------------

    def _set_stage(self, job_id: str, stage: str) -> None:
        self._store.update(job_id, stage=stage, progress=STAGE_PROGRESS.get(stage, 0.0))

    async def _publish_result(self, queue_name: str, body: dict[str, Any]) -> None:
        if self._publish_channel is None:
            raise RuntimeError("AMQP publish channel is not initialised")
        # Persistent + durable queue → ответ переживёт рестарт брокера.
        async with self._publish_lock:
            await self._publish_channel.default_exchange.publish(
                Message(
                    body=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                    content_type="application/json",
                    delivery_mode=DeliveryMode.PERSISTENT,
                ),
                routing_key=queue_name,
            )


# --- bootstrap ---------------------------------------------------------------

def _truthy(value: str | None) -> bool:
    return (value or "").strip().lower() in {"1", "true", "yes", "on"}


def _positive_int(value: str | None, *, default: int) -> int:
    """Парсит положительный int из env; при пустом/битом значении — default."""
    try:
        parsed = int((value or "").strip())
    except ValueError:
        return default
    return parsed if parsed >= 1 else default


def _build_components() -> tuple[Worker | None, JobStateStore, int, bool, Path]:
    # Загружаем .env в os.environ, чтобы переменные worker-а (RABBIT_URL,
    # OBRATKA_QA_ENABLED и пр.), которые мы читаем напрямую через os.environ,
    # тоже подхватывались. pydantic Settings читает .env сам и без этого.
    try:
        from dotenv import load_dotenv
        load_dotenv(override=False)
    except ImportError:
        pass

    settings = get_settings()
    # Разовая настройка логирования + Phoenix. Тот же guard переиспользуется
    # в analyze_payload_llm, поэтому первый job не будет реконфигурировать sink'и.
    ensure_runtime_initialized(settings)

    http_port = int(os.environ.get("LLM_HTTP_PORT", "8000"))
    state_db = os.environ.get("LLM_STATE_DB", "data/job_state.sqlite")
    qa_enabled = _truthy(os.environ.get("OBRATKA_QA_ENABLED"))
    qa_output_dir = Path(os.environ.get("LLM_QA_OUTPUT_DIR", "qa_outputs"))
    max_parallel_jobs = _positive_int(os.environ.get("LLM_MAX_PARALLEL_JOBS"), default=3)

    store = JobStateStore(state_db)

    # AMQP/S3 — опциональны в QA-режиме (когда хочется покрутить пайплайн локально
    # без поднятия PG-стека). В проде RABBIT_URL обязателен.
    rabbit_url = os.environ.get("RABBIT_URL")
    s3_endpoint = os.environ.get("S3_ENDPOINT")
    s3_access = os.environ.get("S3_ACCESS_KEY")
    s3_secret = os.environ.get("S3_SECRET_KEY")
    s3_bucket = os.environ.get("S3_BUCKET", "obratka-jobs")

    have_full_stack = all([rabbit_url, s3_endpoint, s3_access, s3_secret])
    if not have_full_stack:
        if not qa_enabled:
            missing = [
                k for k, v in {
                    "RABBIT_URL":     rabbit_url,
                    "S3_ENDPOINT":    s3_endpoint,
                    "S3_ACCESS_KEY":  s3_access,
                    "S3_SECRET_KEY":  s3_secret,
                }.items() if not v
            ]
            raise RuntimeError(
                "Missing required environment variables: "
                f"{', '.join(missing)}. Set OBRATKA_QA_ENABLED=true to run "
                "in QA-only mode (HTTP only, no RabbitMQ/S3)."
            )
        worker = None
    else:
        s3 = S3Client(
            endpoint_url=s3_endpoint,  # type: ignore[arg-type]
            access_key=s3_access,      # type: ignore[arg-type]
            secret_key=s3_secret,      # type: ignore[arg-type]
            bucket=s3_bucket,
        )
        worker = Worker(
            store=store,
            s3=s3,
            rabbit_url=rabbit_url,  # type: ignore[arg-type]
            max_parallel_jobs=max_parallel_jobs,
        )

    return worker, store, http_port, qa_enabled, qa_output_dir


async def _run_async() -> None:
    worker, store, http_port, qa_enabled, qa_output_dir = _build_components()
    app = make_app(store, qa_enabled=qa_enabled, qa_output_dir=qa_output_dir)

    if qa_enabled:
        print(
            f"[obratka-worker] QA endpoint enabled at POST /qa/analyze, "
            f"outputs -> {qa_output_dir.resolve()}",
            file=sys.stderr,
        )
    if worker is None:
        print(
            "[obratka-worker] Running in QA-only mode (no RabbitMQ/S3 connection).",
            file=sys.stderr,
        )
    config = uvicorn.Config(
        app,
        host="0.0.0.0",
        port=http_port,
        log_level="info",
        access_log=False,
    )
    server = uvicorn.Server(config)

    # Запускаем HTTP и AMQP параллельно. Если любая упадёт — пробрасываем,
    # supervisor (docker restart policy) поднимет процесс заново.
    coros = [server.serve()]
    if worker is not None:
        coros.append(worker.amqp_loop())
    await asyncio.gather(*coros)


def main() -> int:
    try:
        asyncio.run(_run_async())
    except KeyboardInterrupt:
        return 0
    except Exception as e:  # pragma: no cover — crash log
        print(f"Worker fatal error: {type(e).__name__}: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
