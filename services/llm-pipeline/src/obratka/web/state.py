"""SQLite-backed job state store.

Состояние пишется на диск, чтобы PG, опрашивающий `/status/{id}`, видел корректный
прогресс даже после рестарта worker-а. Один процесс — один файл, single-writer
семантика обеспечена `threading.Lock`.

Схема (одна таблица):
    job_state(analysis_job_id PK, status, stage, progress,
              started_at, finished_at, error, updated_at)
"""

from __future__ import annotations

import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Final


_UNSET: Final = object()


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


class JobStateStore:
    """Thin SQLite wrapper. Все операции синхронные и быстрые (<1 ms),
    поэтому вызовы из event loop приемлемы без `asyncio.to_thread`."""

    def __init__(self, db_path: str) -> None:
        Path(db_path).parent.mkdir(parents=True, exist_ok=True)
        # check_same_thread=False допускает обращение из uvicorn-worker-тредов
        # (FastAPI handlers тоже могут попасть в threadpool). Конкурентность
        # сериализуем через self._lock — пишет всегда один writer.
        self._conn = sqlite3.connect(db_path, check_same_thread=False, isolation_level=None)
        self._lock = threading.Lock()
        self._conn.execute("PRAGMA journal_mode=WAL;")
        self._conn.execute("PRAGMA synchronous=NORMAL;")
        self._conn.execute(
            """
            CREATE TABLE IF NOT EXISTS job_state (
                analysis_job_id TEXT PRIMARY KEY,
                status          TEXT NOT NULL,
                stage           TEXT NOT NULL,
                progress        REAL NOT NULL,
                started_at      TEXT NOT NULL,
                finished_at     TEXT,
                error           TEXT,
                updated_at      TEXT NOT NULL
            )
            """
        )

    def start(
        self,
        job_id: str,
        *,
        started_at: str | None = None,
        status: str = "processing",
        stage: str = "received",
        progress: float = 0.0,
    ) -> None:
        """Инициализирует/перезаписывает запись для нового прогона.

        Replay того же `analysis_job_id` сбрасывает состояние — это допустимо
        по идемпотентности из QUICKSTART (S3 PUT перезаписывает output, ответ
        может приходить повторно).
        """
        started = started_at or _now_iso()
        now = _now_iso()
        with self._lock:
            self._conn.execute(
                """
                INSERT INTO job_state
                    (analysis_job_id, status, stage, progress, started_at, finished_at, error, updated_at)
                VALUES (?, ?, ?, ?, ?, NULL, NULL, ?)
                ON CONFLICT(analysis_job_id) DO UPDATE SET
                    status      = excluded.status,
                    stage       = excluded.stage,
                    progress    = excluded.progress,
                    started_at  = excluded.started_at,
                    finished_at = NULL,
                    error       = NULL,
                    updated_at  = excluded.updated_at
                """,
                (job_id, status, stage, progress, started, now),
            )

    def update(
        self,
        job_id: str,
        *,
        status: Any = _UNSET,
        stage: Any = _UNSET,
        progress: Any = _UNSET,
        finished_at: Any = _UNSET,
        error: Any = _UNSET,
    ) -> None:
        """Частичный апдейт. Поля без явного значения не меняются.

        Чтобы стереть `error`, передайте `error=None` (NULL в БД).
        """
        sets: list[str] = []
        params: list[Any] = []
        if status is not _UNSET:
            sets.append("status = ?")
            params.append(status)
        if stage is not _UNSET:
            sets.append("stage = ?")
            params.append(stage)
        if progress is not _UNSET:
            sets.append("progress = ?")
            params.append(progress)
        if finished_at is not _UNSET:
            sets.append("finished_at = ?")
            params.append(finished_at)
        if error is not _UNSET:
            sets.append("error = ?")
            params.append(error)
        if not sets:
            return
        sets.append("updated_at = ?")
        params.append(_now_iso())
        params.append(job_id)
        sql = f"UPDATE job_state SET {', '.join(sets)} WHERE analysis_job_id = ?"
        with self._lock:
            self._conn.execute(sql, params)

    def get(self, job_id: str) -> dict[str, Any] | None:
        with self._lock:
            cur = self._conn.execute(
                """
                SELECT analysis_job_id, status, stage, progress, started_at, finished_at, error
                FROM job_state WHERE analysis_job_id = ?
                """,
                (job_id,),
            )
            row = cur.fetchone()
        if row is None:
            return None
        return {
            "analysis_job_id": row[0],
            "status":          row[1],
            "stage":           row[2],
            "progress":        float(row[3]),
            "started_at":      row[4],
            "finished_at":     row[5],
            "error":           row[6],
        }

    def close(self) -> None:
        with self._lock:
            self._conn.close()
