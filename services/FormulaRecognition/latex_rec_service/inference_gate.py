from __future__ import annotations

import asyncio
import logging
import math
import os
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Callable

from .errors import WORKER_BUSY, WORKER_UNAVAILABLE

LOGGER = logging.getLogger("latex_rec_service.inference")
ProcessTerminator = Callable[[int], Any]


class InferenceGate:
    """One owner thread and one non-queuing slot, independent of HTTP cancellation."""

    def __init__(
        self,
        watchdog_seconds: float = 55.0,
        process_terminator: ProcessTerminator = os._exit,
        startup_watchdog_seconds: float = 120.0,
    ) -> None:
        for name, value in (("watchdog_seconds", watchdog_seconds), ("startup_watchdog_seconds", startup_watchdog_seconds)):
            if not math.isfinite(value) or value <= 0:
                raise ValueError(f"{name} must be finite and positive")
        self._watchdog_seconds = watchdog_seconds
        self._startup_watchdog_seconds = startup_watchdog_seconds
        self._process_terminator = process_terminator
        self._active: asyncio.Task[Any] | None = None
        self._closing: asyncio.Task[None] | None = None
        self._shutdown = False
        self._executor = ThreadPoolExecutor(
            max_workers=1,
            thread_name_prefix="formula-native",
        )

    @property
    def busy(self) -> bool:
        return self._active is not None and not self._active.done()

    async def initialize(self, factory: Callable[[], Any]) -> Any:
        return await self._admit(factory, (), self._startup_watchdog_seconds, "startup")

    async def execute(self, function: Callable[..., Any], *arguments: Any) -> Any:
        return await self._admit(function, arguments, self._watchdog_seconds, "inference")

    async def _admit(self, function, arguments, timeout: float, phase: str) -> Any:
        if self._shutdown:
            raise WORKER_UNAVAILABLE.exception()
        # Event-loop tasks cannot interleave between this check and assignment.
        if self.busy:
            raise WORKER_BUSY.exception()
        self._active = self._submit(function, arguments, timeout, phase)
        return await asyncio.shield(self._active)

    def _submit(self, function, arguments, timeout: float, phase: str) -> asyncio.Task[Any]:
        task = asyncio.create_task(self._execute_native(function, arguments, timeout, phase))
        # Retrieve abandoned failures without changing caller-visible exceptions.
        task.add_done_callback(lambda done: None if done.cancelled() else done.exception())
        return task

    async def _execute_native(self, function, arguments, timeout: float, phase: str) -> Any:
        loop = asyncio.get_running_loop()
        return await loop.run_in_executor(self._executor, self._run_native, function, arguments, timeout, phase)

    def _run_native(self, function, arguments, timeout: float, phase: str) -> Any:
        started = time.monotonic()
        finished = threading.Event()

        def expire() -> None:
            if finished.is_set():
                return
            LOGGER.critical(
                "native_%s_watchdog duration_ms=%.1f worker_pid=%s",
                phase, (time.monotonic() - started) * 1000, os.getpid(),
            )
            self._process_terminator(70)

        # The timer lives with the native call, not its HTTP or event-loop caller.
        timer = threading.Timer(timeout, expire)
        timer.daemon = True
        timer.start()
        try:
            return function(*arguments)
        finally:
            finished.set()
            timer.cancel()

    async def close(self, disposer: Callable[[], None] | None = None) -> None:
        if self._closing is None:
            self._shutdown = True
            self._closing = asyncio.create_task(self._drain_and_dispose(disposer))
            self._closing.add_done_callback(lambda done: None if done.cancelled() else done.exception())
        # Caller cancellation does not abandon native draining/disposal.
        await asyncio.shield(self._closing)

    async def _drain_and_dispose(self, disposer: Callable[[], None] | None) -> None:
        try:
            if self._active is not None:
                try:
                    await asyncio.shield(self._active)
                except Exception:
                    # The original caller owns startup/inference errors; disposal still runs.
                    pass
            if disposer is not None:
                self._active = self._submit(disposer, (), self._watchdog_seconds, "shutdown")
                await asyncio.shield(self._active)
        finally:
            # A blocking shutdown would prevent health checks and cancellation handling.
            self._executor.shutdown(wait=False, cancel_futures=True)
