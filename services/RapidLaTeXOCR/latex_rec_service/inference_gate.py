from __future__ import annotations

import asyncio
import logging
import os
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Callable

from .errors import WORKER_BUSY

LOGGER = logging.getLogger("latex_rec_service.inference")
ProcessTerminator = Callable[[int], Any]


class InferenceGate:
    """One non-queuing native inference slot that survives caller cancellation."""

    def __init__(
        self,
        watchdog_seconds: float = 55.0,
        process_terminator: ProcessTerminator = os._exit,
    ) -> None:
        if watchdog_seconds <= 0:
            raise ValueError("watchdog_seconds must be positive")
        self._watchdog_seconds = watchdog_seconds
        self._process_terminator = process_terminator
        self._active: asyncio.Task[Any] | None = None
        self._shutdown = False
        self._executor = ThreadPoolExecutor(
            max_workers=1,
            thread_name_prefix="latex-inference",
        )

    @property
    def busy(self) -> bool:
        return self._active is not None and not self._active.done()

    async def execute(self, function: Callable[..., Any], *arguments: Any) -> Any:
        if self._shutdown:
            raise RuntimeError("inference gate is shut down")
        # Event-loop tasks cannot interleave between this check and assignment.
        if self.busy:
            raise WORKER_BUSY.exception()
        self._active = asyncio.create_task(self._execute_native(function, *arguments))
        # Keep watchdog and slot ownership independent of the HTTP task, even when
        # the caller is cancelled repeatedly. Retrieve abandoned failures as well.
        self._active.add_done_callback(lambda task: None if task.cancelled() else task.exception())
        return await asyncio.shield(self._active)

    async def _execute_native(self, function: Callable[..., Any], *arguments: Any) -> Any:
        loop = asyncio.get_running_loop()
        native = loop.run_in_executor(self._executor, function, *arguments)
        started = time.monotonic()
        try:
            done, _ = await asyncio.wait({native}, timeout=self._watchdog_seconds)
            if not done:
                await self._terminate_poisoned_runtime(started, native)
            return native.result()
        finally:
            # In production a watchdog exit never returns. Test terminators may return,
            # in which case the slot remains occupied until the native thread finishes.
            if not native.done():
                await asyncio.shield(native)

    async def _terminate_poisoned_runtime(
        self,
        started: float,
        native: asyncio.Future[Any],
    ) -> None:
        LOGGER.critical(
            "native_inference_watchdog duration_ms=%.1f worker_pid=%s",
            (time.monotonic() - started) * 1000,
            os.getpid(),
        )
        self._process_terminator(70)
        if not native.done():
            await asyncio.shield(native)

    def shutdown(self) -> None:
        if self._shutdown:
            return
        self._shutdown = True
        self._executor.shutdown(wait=True, cancel_futures=True)
