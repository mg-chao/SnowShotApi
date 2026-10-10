from __future__ import annotations

import asyncio
import threading
import time
import os
from pathlib import Path
import subprocess
import sys

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
        await gate.close()

    asyncio.run(scenario())


def test_initialization_and_disposal_share_inference_thread_and_close_drains() -> None:
    async def scenario() -> None:
        calls = []
        started = threading.Event()
        release = threading.Event()
        gate = InferenceGate(watchdog_seconds=2)

        def initialize():
            calls.append(("initialize", threading.get_ident()))
            return "ready"

        def native():
            calls.append(("inference", threading.get_ident()))
            started.set()
            assert release.wait(timeout=2)

        assert await gate.initialize(initialize) == "ready"
        caller = asyncio.create_task(gate.execute(native))
        await asyncio.to_thread(started.wait, 1)
        close = asyncio.create_task(gate.close(lambda: calls.append(("dispose", threading.get_ident()))))
        await asyncio.sleep(0)
        assert not close.done()
        with pytest.raises(ServiceError, match="worker_unavailable"):
            await gate.execute(lambda: None)
        # The loop remains responsive throughout draining.
        release.set()
        await caller
        await close
        await gate.close()
        assert [phase for phase, _ in calls] == ["initialize", "inference", "dispose"]
        assert len({owner for _, owner in calls}) == 1

    asyncio.run(scenario())


def test_startup_watchdog_is_independent_of_inference_timeout() -> None:
    async def scenario() -> None:
        exits = []
        gate = InferenceGate(watchdog_seconds=1, startup_watchdog_seconds=.01, process_terminator=exits.append)
        assert await gate.initialize(lambda: (time.sleep(.04), "ready")[1]) == "ready"
        assert exits == [70]
        assert await gate.execute(lambda: "complete") == "complete"
        await gate.close()
    asyncio.run(scenario())


def test_cancelled_close_keeps_draining_and_disposes_once() -> None:
    async def scenario() -> None:
        started = threading.Event()
        release = threading.Event()
        disposed = []
        gate = InferenceGate(watchdog_seconds=2)

        def native():
            started.set()
            assert release.wait(timeout=2)

        caller = asyncio.create_task(gate.execute(native))
        assert await asyncio.to_thread(started.wait, 1)
        close = asyncio.create_task(gate.close(lambda: disposed.append(threading.get_ident())))
        await asyncio.sleep(0)
        close.cancel()
        with pytest.raises(asyncio.CancelledError):
            await close
        with pytest.raises(ServiceError, match="worker_unavailable"):
            await gate.execute(lambda: None)
        release.set()
        await caller
        await gate.close()
        assert len(disposed) == 1
    asyncio.run(scenario())


@pytest.mark.parametrize("phase", ["startup", "inference", "shutdown"])
def test_real_watchdog_exits_even_while_event_loop_is_blocked(phase: str) -> None:
    service_root = Path(__file__).resolve().parents[1]
    script = """
import asyncio, threading, time
from latex_rec_service.inference_gate import InferenceGate

async def main():
    gate = InferenceGate(watchdog_seconds=.05, startup_watchdog_seconds=.05)
    started = threading.Event()
    def hang():
        started.set()
        time.sleep(30)
    if PHASE == 'startup':
        caller = asyncio.create_task(gate.initialize(hang))
    elif PHASE == 'shutdown':
        caller = asyncio.create_task(gate.close(hang))
    else:
        caller = asyncio.create_task(gate.execute(hang))
    await asyncio.to_thread(started.wait, 2)
    caller.cancel()
    time.sleep(30)
asyncio.run(main())
""".replace("PHASE", repr(phase))
    environment = os.environ.copy()
    environment["PYTHONPATH"] = str(service_root)
    completed = subprocess.run([sys.executable, "-c", script], env=environment, capture_output=True, timeout=5)
    assert completed.returncode == 70, completed.stderr.decode()


@pytest.mark.parametrize("value", [0, -1, float("inf"), float("nan")])
def test_watchdogs_require_finite_positive_values(value):
    with pytest.raises(ValueError):
        InferenceGate(watchdog_seconds=value)
    with pytest.raises(ValueError):
        InferenceGate(startup_watchdog_seconds=value)


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
        await gate.close()

    asyncio.run(scenario())


def test_watchdog_terminates_process_when_native_runtime_exceeds_limit() -> None:
    async def scenario() -> None:
        exits: list[int] = []
        gate = InferenceGate(watchdog_seconds=0.01, process_terminator=exits.append)

        result = await gate.execute(lambda: (time.sleep(0.03), "done")[1])

        assert result == "done"
        assert exits == [70]
        await gate.close()

    asyncio.run(scenario())
