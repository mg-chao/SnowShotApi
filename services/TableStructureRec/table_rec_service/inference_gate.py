from __future__ import annotations

import asyncio
import logging
import os
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Callable

from .errors import WORKER_BUSY

LOGGER = logging.getLogger("table_rec_service.inference")
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
        self._busy = False
        self._shutdown = False
        self._executor = ThreadPoolExecutor(
            max_workers=1,
            thread_name_prefix="table-inference",
        )

    @property
    def busy(self) -> bool:
        return self._busy

    async def execute(self, function: Callable[..., Any], *arguments: Any) -> Any:
        if self._shutdown:
            raise RuntimeError("inference gate is shut down")
        # Event-loop tasks cannot interleave between this check and assignment.
        if self._busy:
            raise WORKER_BUSY.exception()
        self._busy = True
        loop = asyncio.get_running_loop()
        native = loop.run_in_executor(self._executor, function, *arguments)
        started = time.monotonic()
        try:
            done, _ = await asyncio.wait({native}, timeout=self._watchdog_seconds)
            if not done:
                await self._terminate_poisoned_runtime(started, native)
            return native.result()
        except asyncio.CancelledError:
            # The request may disappear, but the DirectML call is still using the process.
            remaining = max(0.0, self._watchdog_seconds - (time.monotonic() - started))
            done, _ = await asyncio.wait({native}, timeout=remaining)
            if not done:
                await self._terminate_poisoned_runtime(started, native)
            raise
        finally:
            # In production a watchdog exit never returns. Test terminators may return,
            # in which case the slot remains occupied until the native thread finishes.
            if not native.done():
                await asyncio.shield(native)
            self._busy = False

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
