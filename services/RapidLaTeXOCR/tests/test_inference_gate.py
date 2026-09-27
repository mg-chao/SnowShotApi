from __future__ import annotations

import asyncio
import threading
import time

import pytest

from latex_rec_service.errors import ServiceError
from latex_rec_service.inference_gate import InferenceGate


def test_cancelled_request_retains_slot_until_native_inference_finishes() -> None:
    async def scenario() -> None:
        started = threading.Event()
        release = threading.Event()

        def native() -> str:
            started.set()
            release.wait(timeout=2)
            return "complete"

        gate = InferenceGate(watchdog_seconds=1, process_terminator=lambda _code: None)
        first = asyncio.create_task(gate.execute(native))
        await asyncio.to_thread(started.wait, 1)
        first.cancel()
        await asyncio.sleep(0)
        assert gate.busy
        with pytest.raises(ServiceError, match="worker_busy"):
            await gate.execute(lambda: "unexpected")
        release.set()
        with pytest.raises(asyncio.CancelledError):
            await first
        for _ in range(100):
            if not gate.busy:
                break
            await asyncio.sleep(0.01)
        assert not gate.busy
        gate.shutdown()

    asyncio.run(scenario())


def test_abandoned_inference_keeps_watchdog_and_recovers_after_native_failure() -> None:
    async def scenario() -> None:
        exits: list[int] = []
        release = threading.Event()
        started = threading.Event()

        def native():
            started.set()
            release.wait(timeout=1)
            raise ValueError("native failed after disconnect")

        gate = InferenceGate(watchdog_seconds=0.03, process_terminator=exits.append)
        caller = asyncio.create_task(gate.execute(native))
        await asyncio.to_thread(started.wait, 1)
        caller.cancel()
        caller.cancel()
        with pytest.raises(asyncio.CancelledError):
            await caller
        await asyncio.sleep(0.06)
        assert exits == [70]
        assert gate.busy
        release.set()
        for _ in range(100):
            if not gate.busy:
                break
            await asyncio.sleep(0.01)
        assert await gate.execute(lambda: "recovered") == "recovered"
        gate.shutdown()

    asyncio.run(scenario())


def test_watchdog_terminates_process_when_native_runtime_exceeds_limit() -> None:
    async def scenario() -> None:
        exits: list[int] = []
        gate = InferenceGate(watchdog_seconds=0.01, process_terminator=exits.append)

        result = await gate.execute(lambda: (time.sleep(0.03), "done")[1])

        assert result == "done"
        assert exits == [70]
        gate.shutdown()

    asyncio.run(scenario())
