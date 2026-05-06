"""Async-friendly обёртка над boto3 для S3-совместимого MinIO.

boto3 — синхронный, поэтому каждое сетевое обращение прокидываем через
`asyncio.to_thread`, чтобы не блокировать event loop AMQP-консьюмера и
FastAPI. Path-style addressing — обязателен для MinIO.
"""

from __future__ import annotations

import asyncio
import json
from typing import Any
from urllib.parse import urlparse

import boto3
from botocore.config import Config


class S3Client:
    def __init__(
        self,
        *,
        endpoint_url: str,
        access_key: str,
        secret_key: str,
        bucket: str,
        region: str = "us-east-1",
    ) -> None:
        # MinIO игнорирует регион, но AWS SDK требует поле. Path-style обязателен:
        # MinIO не умеет virtual-hosted (`<bucket>.s3.endpoint`).
        self._client = boto3.client(
            "s3",
            endpoint_url=endpoint_url,
            aws_access_key_id=access_key,
            aws_secret_access_key=secret_key,
            region_name=region,
            config=Config(
                s3={"addressing_style": "path"},
                signature_version="s3v4",
                retries={"max_attempts": 3, "mode": "standard"},
            ),
        )
        self._bucket = bucket

    @property
    def bucket(self) -> str:
        return self._bucket

    @staticmethod
    def parse_url(url: str) -> tuple[str, str]:
        """Парсит `s3://<bucket>/<key>` → (bucket, key)."""
        parsed = urlparse(url)
        if parsed.scheme != "s3":
            raise ValueError(f"Expected s3:// URL, got: {url!r}")
        if not parsed.netloc or not parsed.path.lstrip("/"):
            raise ValueError(f"Malformed s3 URL (missing bucket or key): {url!r}")
        return parsed.netloc, parsed.path.lstrip("/")

    async def get_json(self, url: str) -> dict[str, Any]:
        bucket, key = self.parse_url(url)
        return await asyncio.to_thread(self._get_json_sync, bucket, key)

    def _get_json_sync(self, bucket: str, key: str) -> dict[str, Any]:
        resp = self._client.get_object(Bucket=bucket, Key=key)
        body = resp["Body"].read()
        return json.loads(body.decode("utf-8"))

    async def put_json(self, key: str, payload: dict[str, Any]) -> str:
        """Кладёт JSON по `key` в дефолтный bucket. Возвращает `s3://...` URL."""
        await asyncio.to_thread(self._put_json_sync, key, payload)
        return f"s3://{self._bucket}/{key}"

    def _put_json_sync(self, key: str, payload: dict[str, Any]) -> None:
        # ensure_ascii=False — кириллица как есть, согласовано с PG (UTF-8 без \uXXXX).
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self._client.put_object(
            Bucket=self._bucket,
            Key=key,
            Body=body,
            ContentType="application/json; charset=utf-8",
        )
