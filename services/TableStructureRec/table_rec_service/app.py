from __future__ import annotations

import asyncio
import inspect
import logging
import os
import time
from contextlib import asynccontextmanager
from typing import Any, Callable

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from .config import MAX_UPLOAD_BYTES, ServiceSettings
from .engine import EngineBundle
from .errors import (
    INFERENCE_FAILED,
    INVALID_IMAGE,
    NO_TABLE,
    PAYLOAD_TOO_LARGE,
    ServiceError,
    WORKER_UNAVAILABLE,
)
from .html_sanitizer import sanitize_table_html
from .image_validation import decode_webp
from .inference_gate import InferenceGate, ProcessTerminator

LOGGER = logging.getLogger("table_rec_service.http")
BundleFactory = Callable[[], Any]


class HealthResponse(BaseModel):
    status: str


class TableSuccessResponse(BaseModel):
    html: str


class ErrorDetail(BaseModel):
    code: str
    message: str


class ErrorResponse(BaseModel):
    error: ErrorDetail


ERROR_RESPONSES = {
    413: {"model": ErrorResponse, "description": "Payload too large"},
    415: {"model": ErrorResponse, "description": "Unsupported image format"},
    422: {"model": ErrorResponse, "description": "Invalid image or no table"},
    500: {"model": ErrorResponse, "description": "Inference failure"},
    503: {"model": ErrorResponse, "description": "Worker busy or unavailable"},
}
EXTRACT_OPENAPI = {
    "parameters": [
        {
            "name": "X-Operation-ID",
            "in": "header",
            "required": False,
            "schema": {"type": "string", "maxLength": 64},
        },
        {
            "name": "X-Request-ID",
            "in": "header",
            "required": False,
            "schema": {"type": "string", "maxLength": 64},
        },
    ],
    "requestBody": {
        "required": True,
        "content": {
            "image/webp": {
                "schema": {
                    "type": "string",
                    "format": "binary",
                }
            }
        },
    },
}


def create_app(
    bundle_factory: BundleFactory | None = None,
    watchdog_seconds: float | None = None,
    process_terminator: ProcessTerminator = os._exit,
) -> FastAPI:
    settings = ServiceSettings.from_environment()
    factory = bundle_factory or (lambda: EngineBundle.load(settings.model_dir))

    @asynccontextmanager
    async def lifespan(application: FastAPI):
        LOGGER.info("worker_starting worker_pid=%s", os.getpid())
        application.state.ready = False
        application.state.inference_gate = InferenceGate(
            settings.watchdog_seconds if watchdog_seconds is None else watchdog_seconds,
            process_terminator,
        )
        try:
            bundle = await asyncio.to_thread(factory)
            if inspect.isawaitable(bundle):
                bundle = await bundle
            application.state.engine_bundle = bundle
            application.state.ready = bool(getattr(bundle, "ready", True))
            LOGGER.info(
                "worker_ready worker_pid=%s ready=%s providers=%s",
                os.getpid(),
                application.state.ready,
                getattr(bundle, "provider_summary", {}),
            )
            yield
        finally:
            application.state.ready = False
            application.state.inference_gate.shutdown()
            LOGGER.info("worker_stopped worker_pid=%s", os.getpid())

    application = FastAPI(
        title="TableStructureRec",
        version="2.0.0",
        docs_url=None,
        redoc_url=None,
        lifespan=lifespan,
    )

    @application.exception_handler(ServiceError)
    async def handle_service_error(_request: Request, exception: ServiceError):
        return JSONResponse(
            status_code=exception.status_code,
            content={
                "error": {
                    "code": exception.code,
                    "message": exception.public_message,
                }
            },
        )

    @application.get("/health/live", response_model=HealthResponse)
    async def live() -> HealthResponse:
        return HealthResponse(status="live")

    @application.get(
        "/health/ready",
        response_model=HealthResponse,
        responses={503: {"model": HealthResponse, "description": "Not ready"}},
    )
    async def ready(request: Request):
        is_ready = bool(getattr(request.app.state, "ready", False))
        return JSONResponse(
            status_code=200 if is_ready else 503,
            content={"status": "ready" if is_ready else "not_ready"},
        )

    @application.post(
        "/v2/table/extract",
        response_model=TableSuccessResponse,
        responses=ERROR_RESPONSES,
        openapi_extra=EXTRACT_OPENAPI,
    )
    async def extract(request: Request):
        request_id = _request_id(request)
        operation_id = _operation_id(request)
        started_at = time.perf_counter()
        table_type = "unknown"
        failure_category = "none"

        try:
            if not bool(getattr(request.app.state, "ready", False)):
                raise WORKER_UNAVAILABLE.exception()
            payload = await _read_webp_body(request)
            image = decode_webp(payload)
            result = await request.app.state.inference_gate.execute(
                request.app.state.engine_bundle.extract,
                image,
            )
            sanitized_html = sanitize_table_html(result.html or "")
            if not sanitized_html:
                raise NO_TABLE.exception()
            table_type = result.table_type
            return {"html": sanitized_html}
        except ServiceError as exception:
            failure_category = exception.category
            raise
        except Exception as exception:
            failure_category = INFERENCE_FAILED.category
            raise INFERENCE_FAILED.exception() from exception
        finally:
            providers = getattr(
                getattr(request.app.state, "engine_bundle", None),
                "provider_summary",
                {},
            )
            LOGGER.info(
                "table_extract request_id=%s operation_id=%s duration_ms=%.1f table_type=%s "
                "worker_pid=%s providers=%s failure_category=%s",
                request_id,
                operation_id,
                (time.perf_counter() - started_at) * 1000,
                table_type,
                os.getpid(),
                providers,
                failure_category,
            )

    return application


def _declared_content_length(request: Request) -> int | None:
    value = request.headers.get("content-length")
    if value is None:
        return None
    try:
        length = int(value)
    except ValueError as exception:
        raise INVALID_IMAGE.exception() from exception
    if length < 0:
        raise INVALID_IMAGE.exception()
    if length > MAX_UPLOAD_BYTES:
        raise PAYLOAD_TOO_LARGE.exception()
    return length


async def _read_webp_body(request: Request) -> bytes:
    if request.headers.get("content-type", "").lower() != "image/webp":
        from .errors import NOT_WEBP
        raise NOT_WEBP.exception()
    declared = _declared_content_length(request)
    payload = bytearray()
    async for chunk in request.stream():
        if not chunk:
            continue
        if len(chunk) > MAX_UPLOAD_BYTES - len(payload):
            raise PAYLOAD_TOO_LARGE.exception()
        payload.extend(chunk)
    if declared is not None and len(payload) != declared:
        raise INVALID_IMAGE.exception()
    if not payload:
        raise INVALID_IMAGE.exception()
    return bytes(payload)


def _request_id(request: Request) -> str:
    value = request.headers.get("X-Request-ID", "")
    if not value or len(value) > 64 or not value.isprintable():
        return "unassigned"
    return value


def _operation_id(request: Request) -> str:
    value = request.headers.get("X-Operation-ID", "")
    if not value or len(value) > 64 or not value.isascii() or not value.isprintable():
        return "unassigned"
    return value


app = create_app()
