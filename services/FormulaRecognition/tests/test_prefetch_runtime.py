from __future__ import annotations

import asyncio
import hashlib
import json
import os
import subprocess
import sys
import threading
import types
from pathlib import Path

import pytest
from filelock import FileLock

from latex_rec_service import prefetch
from latex_rec_service.model_manifest import ARCHIVE_ROOT, MODEL_NAME


@pytest.fixture(autouse=True)
def valid_environment(monkeypatch):
    monkeypatch.delenv("LATEX_REC_EXECUTION_PROVIDER", raising=False)
    monkeypatch.setenv("LATEX_REC_CPU_THREADS", "1")
    monkeypatch.setenv("LATEX_REC_WATCHDOG_SECONDS", "55")
    monkeypatch.setenv("LATEX_REC_STARTUP_WATCHDOG_SECONDS", "120")
    for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS", "TOKENIZERS_PARALLELISM"):
        monkeypatch.setenv(name, "9")


@pytest.mark.parametrize("name,value", [
    ("LATEX_REC_CPU_THREADS", "0"), ("LATEX_REC_CPU_THREADS", "-1"),
    ("LATEX_REC_CPU_THREADS", "257"), ("LATEX_REC_CPU_THREADS", "cpu"),
    ("LATEX_REC_WATCHDOG_SECONDS", "0"), ("LATEX_REC_WATCHDOG_SECONDS", "56"),
    ("LATEX_REC_WATCHDOG_SECONDS", "inf"), ("LATEX_REC_STARTUP_WATCHDOG_SECONDS", "0"),
    ("LATEX_REC_STARTUP_WATCHDOG_SECONDS", "301"), ("LATEX_REC_STARTUP_WATCHDOG_SECONDS", "nan"),
    ("LATEX_REC_EXECUTION_PROVIDER", "cpu"), ("LATEX_REC_EXECUTION_PROVIDER", ""),
])
def test_invalid_runtime_is_rejected_before_lock_or_download(tmp_path, monkeypatch, name, value):
    monkeypatch.setenv(name, value)
    monkeypatch.setattr(prefetch, "FileLock", lambda *_args, **_kwargs: pytest.fail("Invalid settings acquired a model lock"))
    monkeypatch.setattr(prefetch.requests, "get", lambda *_args, **_kwargs: pytest.fail("Invalid settings downloaded models"))
    with pytest.raises(ValueError, match=name):
        prefetch.prefetch(tmp_path / "missing-parent" / "model")
    assert not (tmp_path / "missing-parent").exists()


def test_prefetch_honors_affinity_cpu_limit(monkeypatch):
    monkeypatch.setattr(prefetch, "available_cpu_count", lambda: 2)
    monkeypatch.setenv("LATEX_REC_CPU_THREADS", "3")
    with pytest.raises(ValueError, match="between 1 and 2"):
        prefetch._runtime_limits()
    with pytest.raises(ValueError, match="between 1 and 2"):
        prefetch._preflight(Path("unused"), 3)


@pytest.mark.parametrize("threads", [0, -1, True, 1.0, 257])
def test_direct_preflight_rejects_invalid_cpu_threads(threads):
    with pytest.raises(ValueError, match="LATEX_REC_CPU_THREADS"):
        prefetch._preflight(Path("unused"), threads)


def test_preflight_load_and_disposal_share_owner_thread(monkeypatch, tmp_path):
    caller = threading.get_ident()
    calls = []
    gate_options = []
    original_gate = prefetch.InferenceGate

    def gate(**options):
        gate_options.append(options)
        return original_gate(**options)

    def load(path, threads):
        assert path == tmp_path and threads == 1
        assert all(os.environ[name] == "1" for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"))
        assert os.environ["TOKENIZERS_PARALLELISM"] == "false"
        owner = threading.get_ident()
        calls.append(("load", owner))
        return types.SimpleNamespace(close=lambda: calls.append(("close", threading.get_ident())))

    monkeypatch.setattr(prefetch, "InferenceGate", gate)
    monkeypatch.setitem(sys.modules, "latex_rec_service.engine", types.SimpleNamespace(EngineBundle=types.SimpleNamespace(load=load)))
    prefetch._preflight(tmp_path, 1)
    assert gate_options == [{"watchdog_seconds": 55, "startup_watchdog_seconds": 120}]
    assert calls[0][0] == "load" and calls[1][0] == "close"
    assert calls[0][1] == calls[1][1] and calls[0][1] != caller


def test_preflight_load_failure_is_propagated(monkeypatch, tmp_path):
    def load(*_args):
        raise RuntimeError("native startup failed")

    monkeypatch.setitem(sys.modules, "latex_rec_service.engine", types.SimpleNamespace(EngineBundle=types.SimpleNamespace(load=load)))
    with pytest.raises(RuntimeError, match="native startup failed"):
        prefetch._preflight(tmp_path, 1)


def test_cancelled_preflight_closes_eventual_bundle_on_owner_thread(monkeypatch, tmp_path):
    started = threading.Event()
    release = threading.Event()
    calls = []
    original_run = asyncio.run

    def load(*_args):
        owner = threading.get_ident()
        calls.append(("load", owner))
        started.set()
        assert release.wait(timeout=2), "Canceled startup was not released"
        return types.SimpleNamespace(close=lambda: calls.append(("close", threading.get_ident())))

    async def cancel_startup(coroutine):
        task = asyncio.create_task(coroutine)
        try:
            assert await asyncio.to_thread(started.wait, 2), "Native startup did not begin"
            task.cancel()
            await asyncio.sleep(0)
        finally:
            release.set()
        with pytest.raises(asyncio.CancelledError):
            await task
        assert len(calls) == 2
        assert calls[0][0] == "load" and calls[1][0] == "close"
        assert calls[0][1] == calls[1][1] and calls[0][1] != threading.get_ident()

    monkeypatch.setitem(sys.modules, "latex_rec_service.engine", types.SimpleNamespace(EngineBundle=types.SimpleNamespace(load=load)))
    monkeypatch.setattr(prefetch.asyncio, "run", lambda coroutine: original_run(cancel_startup(coroutine)))
    prefetch._preflight(tmp_path, 1)


@pytest.mark.parametrize("phase", ["startup", "shutdown"])
def test_native_preflight_hang_exits_process_and_releases_model_lock(tmp_path, phase):
    destination = tmp_path / "models"
    destination.mkdir()
    files = []
    for name in ("inference.json", "inference.pdiparams", "inference.yml"):
        payload = name.encode()
        (destination / name).write_bytes(payload)
        files.append({"path": name, "size_bytes": len(payload), "sha256": hashlib.sha256(payload).hexdigest()})
    manifest_path = tmp_path / "manifest.json"
    manifest_path.write_text(json.dumps({
        "schema_version": 1, "model_name": MODEL_NAME,
        "archive": {"url": "https://models.test/model.tar", "root": ARCHIVE_ROOT, "size_bytes": 1000, "sha256": "0" * 64},
        "files": files,
    }))
    child = r'''
import os, sys, threading, time, types
from pathlib import Path
from latex_rec_service import prefetch
assert "numpy" not in sys.modules and "paddle" not in sys.modules
phase, destination, manifest = sys.argv[1:]
def load(path, threads):
    assert threads == 1
    assert all(os.environ[name] == "1" for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"))
    assert os.environ["TOKENIZERS_PARALLELISM"] == "false"
    owner = threading.get_ident()
    if phase == "startup":
        time.sleep(30)
    def close():
        assert threading.get_ident() == owner
        time.sleep(30)
    return types.SimpleNamespace(close=close)
sys.modules["latex_rec_service.engine"] = types.SimpleNamespace(EngineBundle=types.SimpleNamespace(load=load))
prefetch.prefetch(Path(destination), Path(manifest))
'''
    environment = os.environ.copy()
    environment["PYTHONPATH"] = str(Path(__file__).resolve().parents[1])
    environment["LATEX_REC_STARTUP_WATCHDOG_SECONDS"] = "1"
    environment["LATEX_REC_WATCHDOG_SECONDS"] = "1"
    completed = subprocess.run(
        [sys.executable, "-c", child, phase, str(destination), str(manifest_path)],
        env=environment, capture_output=True, text=True, timeout=15,
    )
    assert completed.returncode == 70, completed.stderr
    assert f"native_{phase}_watchdog" in completed.stderr
    assert sorted(path.name for path in destination.iterdir()) == sorted(item["path"] for item in files)
    # The native process terminated while holding this lock. A subsequent
    # installation must still be able to acquire it without manual recovery.
    with FileLock(str(tmp_path / ".models.prefetch.lock"), timeout=0):
        pass
