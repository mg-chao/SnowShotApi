from __future__ import annotations

import os
from pathlib import Path
import subprocess
import sys

import pytest


@pytest.mark.skipif(sys.platform != "linux", reason="Linux native signal replacement")
@pytest.mark.parametrize("outcome", ["failed", "canceled"])
def test_startup_cleanup_restores_python_sigterm_after_native_override(outcome):
    child = r'''
import asyncio, ctypes, os, signal, sys, threading
from latex_rec_service.app import create_app

outcome = sys.argv[1]
received, calls = [], []
started, release = threading.Event(), threading.Event()
def termination_handler(sig, frame):
    received.append(sig)
signal.signal(signal.SIGTERM, termination_handler)
original_mask = signal.pthread_sigmask(signal.SIG_BLOCK, set())

class Bundle:
    ready = True
    def close(self):
        calls.append(("close", threading.get_ident()))

def factory():
    assert signal.SIGTERM in signal.pthread_sigmask(signal.SIG_BLOCK, set())
    # Reproduce Paddle's C-level replacement without modifying Python's cache.
    library = ctypes.CDLL(None)
    library.signal.argtypes = [ctypes.c_int, ctypes.c_void_p]
    library.signal.restype = ctypes.c_void_p
    library.signal(signal.SIGTERM, ctypes.c_void_p(0))
    assert signal.getsignal(signal.SIGTERM) is termination_handler
    calls.append(("native_override", threading.get_ident()))
    if outcome == "failed":
        raise RuntimeError("startup failed")
    started.set()
    assert release.wait(5)
    calls.append(("initialized", threading.get_ident()))
    return Bundle()

async def run():
    application = create_app(factory, watchdog_seconds=2, startup_watchdog_seconds=2)
    lifespan = application.router.lifespan_context(application)
    if outcome == "failed":
        try:
            await lifespan.__aenter__()
        except RuntimeError as exception:
            assert str(exception) == "startup failed"
        else:
            raise AssertionError("Startup failure was swallowed")
    else:
        task = asyncio.create_task(lifespan.__aenter__())
        try:
            assert await asyncio.to_thread(started.wait, 1)
            task.cancel()
            await asyncio.sleep(0)
        finally:
            release.set()
        try:
            await task
        except asyncio.CancelledError:
            pass
        else:
            raise AssertionError("Startup cancellation was swallowed")
    assert not application.state.ready
asyncio.run(run())
assert signal.pthread_sigmask(signal.SIG_BLOCK, set()) == original_mask

# A cached-identity comparison alone would leave the OS action at SIG_DFL and
# kill this child here. Verify the restored Python handler actually receives it.
os.kill(os.getpid(), signal.SIGTERM)
assert received == [signal.SIGTERM]
assert len({owner for _, owner in calls}) == 1
assert calls[0][1] != threading.get_ident()
expected = ["native_override"] if outcome == "failed" else ["native_override", "initialized", "close"]
assert [phase for phase, _ in calls] == expected
'''
    environment = os.environ.copy()
    environment["PYTHONPATH"] = str(Path(__file__).resolve().parents[1])
    environment["LATEX_REC_ENVIRONMENT"] = "development"
    completed = subprocess.run(
        [sys.executable, "-c", child, outcome], env=environment,
        capture_output=True, text=True, timeout=15,
    )
    assert completed.returncode == 0, completed.stderr
