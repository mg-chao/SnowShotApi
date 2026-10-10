from __future__ import annotations

import asyncio
import logging
import os
import signal
import threading
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
    NO_FORMULA,
    PAYLOAD_TOO_LARGE,
    ServiceError,
    WORKER_UNAVAILABLE,
)
from .image_validation import decode_webp
from .inference_gate import InferenceGate, ProcessTerminator

LOGGER = logging.getLogger("latex_rec_service.http")
BundleFactory = Callable[[], Any]


class HealthResponse(BaseModel):
    status: str


class LatexSuccessResponse(BaseModel):
    latex: str


class ErrorDetail(BaseModel):
    code: str
    message: str


class ErrorResponse(BaseModel):
    error: ErrorDetail


ERROR_RESPONSES = {
    413: {"model": ErrorResponse, "description": "Payload too large"},
    415: {"model": ErrorResponse, "description": "Unsupported image format"},
    422: {"model": ErrorResponse, "description": "Invalid image or no latex"},
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
    startup_watchdog_seconds: float | None = None,
) -> FastAPI:
    settings = ServiceSettings.from_environment()
    factory = bundle_factory or (
        lambda: EngineBundle.load(settings.model_dir, settings.cpu_threads)
    )

    @asynccontextmanager
    async def lifespan(application: FastAPI):
        LOGGER.info("worker_starting worker_pid=%s", os.getpid())
        application.state.ready = False
        # Paddle's native initialization replaces the OS SIGTERM handler without
        # updating Python's cached handler. Keep the server's handler and explicitly
        # register it again on the main thread before advertising readiness.
        termination_handler = (
            signal.getsignal(signal.SIGTERM)
            if threading.current_thread() is threading.main_thread()
            else None
        )
        application.state.inference_gate = InferenceGate(
            settings.watchdog_seconds if watchdog_seconds is None else watchdog_seconds,
            process_terminator,
            settings.startup_watchdog_seconds if startup_watchdog_seconds is None else startup_watchdog_seconds,
        )
        bundle = None
        startup_signal_mask = None

        def restore_termination_handler() -> None:
            nonlocal startup_signal_mask
            try:
                if termination_handler is not None:
                    signal.signal(signal.SIGTERM, termination_handler)
            finally:
                if startup_signal_mask is not None:
                    signal.pthread_sigmask(signal.SIG_SETMASK, startup_signal_mask)
                    startup_signal_mask = None

        def initialize_bundle():
            nonlocal bundle
            bundle = factory()
            return bundle

        def dispose_bundle() -> None:
            # Startup may finish after its caller is cancelled. Resolve disposal
            # after draining, on the owner thread, so that bundle is still released.
            close = getattr(bundle, "close", None)
            if close is not None:
                close()

        try:
            if termination_handler is not None and hasattr(signal, "pthread_sigmask"):
                # The executor starts lazily below. Its owner, native workers and
                # watchdog timers inherit this mask, so Paddle cannot consume TERM
                # while it replaces the process handler during initialization.
                startup_signal_mask = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGTERM})
            bundle = await application.state.inference_gate.initialize(initialize_bundle)
            restore_termination_handler()
            application.state.engine_bundle = bundle
            application.state.ready = bool(getattr(bundle, "ready", True))
            LOGGER.info(
                "worker_ready worker_pid=%s ready=%s runtime=%s",
                os.getpid(),
                application.state.ready,
                getattr(bundle, "runtime_summary", {}),
            )
            yield
        finally:
            application.state.ready = False
            try:
                await application.state.inference_gate.close(dispose_bundle)
            finally:
                # Failed or canceled startup can still finish native initialization
                # while draining; restore ownership after its eventual cleanup too.
                restore_termination_handler()
            LOGGER.info("worker_stopped worker_pid=%s", os.getpid())

    application = FastAPI(
        title="SnowShot Formula Recognition",
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
        "/v2/latex/extract",
        response_model=LatexSuccessResponse,
        responses=ERROR_RESPONSES,
        openapi_extra=EXTRACT_OPENAPI,
    )
    async def extract(request: Request):
        request_id = _request_id(request)
        operation_id = _operation_id(request)
        started_at = time.perf_counter()
        failure_category = "none"

        try:
            if not bool(getattr(request.app.state, "ready", False)):
                raise WORKER_UNAVAILABLE.exception()
            payload = await _read_webp_body(request)
            result = await request.app.state.inference_gate.execute(
                _extract_payload,
                request.app.state.engine_bundle,
                payload,
            )
            latex = result.latex or ""
            if not latex.strip():
                raise NO_FORMULA.exception()
            return {"latex": latex}
        except ServiceError as exception:
            failure_category = exception.category
            raise
        except asyncio.CancelledError:
            failure_category = "caller_cancelled"
            raise
        except Exception as exception:
            failure_category = INFERENCE_FAILED.category
            raise INFERENCE_FAILED.exception() from exception
        finally:
            runtime = getattr(
                getattr(request.app.state, "engine_bundle", None),
                "runtime_summary",
                {},
            )
            LOGGER.info(
                "latex_extract request_id=%s operation_id=%s duration_ms=%.1f "
                "worker_pid=%s runtime=%s failure_category=%s",
                request_id,
                operation_id,
                (time.perf_counter() - started_at) * 1000,
                os.getpid(),
                runtime,
                failure_category,
            )

    return application


def _extract_payload(bundle: Any, payload: bytes):
    # Admission occurs before expensive decode, and the event loop stays free for health checks.
    return bundle.extract(decode_webp(payload))


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
