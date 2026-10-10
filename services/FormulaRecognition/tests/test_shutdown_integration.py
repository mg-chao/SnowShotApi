from __future__ import annotations

import json
import os
import signal
import socket
import ssl
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import httpx
import pytest

from latex_rec_service.config import ServiceSettings
from tests.test_mtls_smoke import _create_test_pki


ROOT = Path(__file__).resolve().parents[1]
pytestmark = [
    pytest.mark.recognition,
    pytest.mark.skipif(sys.platform != "linux", reason="Linux process SIGTERM lifecycle"),
]

# Run Uvicorn on its real main thread. Paddle is first imported by the native
# owner during startup, after Uvicorn has installed its process signal handlers.
CHILD = r'''
import json, sys, threading, time
from pathlib import Path
from latex_rec_service.__main__ import main
from latex_rec_service.engine import EngineBundle

mode, event_file, release_file = sys.argv[1:]
event_file, release_file = Path(event_file), Path(release_file)
topology_file = event_file.with_name("signals.json")
topology = {}
def signal_topology(phase):
    threads = {}
    for path in Path("/proc/self/task").iterdir():
        status = (path / "status").read_text()
        mask = next(line.split()[1] for line in status.splitlines() if line.startswith("SigBlk:"))
        threads[path.name] = int(mask, 16)
    topology[phase] = threads
    topology_file.write_text(json.dumps(topology), encoding="utf-8")
signal_topology("before_server")

def record(event):
    with event_file.open("a", encoding="utf-8") as output:
        output.write(json.dumps({"event": event, "thread": threading.get_ident()}) + "\n")
        output.flush()

original_load = EngineBundle.load
def observed_load(cls, model_dir, cpu_threads):
    bundle = original_load(model_dir, cpu_threads)
    signal_topology("native_loaded")
    record("initialized")
    class ObservedBundle:
        ready = bundle.ready
        runtime_summary = bundle.runtime_summary
        def extract(self, image):
            record("extract_started")
            if mode == "inflight":
                deadline = time.monotonic() + 30
                while not release_file.is_file():
                    if time.monotonic() >= deadline:
                        raise RuntimeError("Parent did not release the admitted request")
                    time.sleep(0.01)
            result = bundle.extract(image)
            record("extract_finished")
            return result
        def close(self):
            signal_topology("disposal")
            record("close_started")
            if mode == "blocked_disposal":
                threading.Event().wait()
            bundle.close()
            record("close_finished")
    if mode == "startup":
        record("startup_held")
        deadline = time.monotonic() + 30
        while not release_file.is_file():
            if time.monotonic() >= deadline:
                raise RuntimeError("Parent did not release native startup")
            time.sleep(0.01)
    return ObservedBundle()

EngineBundle.load = classmethod(observed_load)
main()
'''


@pytest.fixture(scope="module")
def shutdown_pki(tmp_path_factory):
    directory = tmp_path_factory.mktemp("shutdown-pki")
    return _create_test_pki(directory)


def _events(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text().splitlines()] if path.is_file() else []


def _port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def _wait_ready(process, client, endpoint, log):
    deadline = time.monotonic() + 150
    while time.monotonic() < deadline:
        assert process.poll() is None, log.read_text()[-4000:]
        try:
            response = client.get(endpoint + "/health/ready")
            if response.status_code == 200:
                assert response.json() == {"status": "ready"}
                return
        except httpx.TransportError:
            pass
        time.sleep(0.05)
    pytest.fail("Real formula server did not become ready: " + log.read_text()[-4000:])


def _wait_event(process, events, name, log, timeout):
    deadline = time.monotonic() + timeout
    while name not in [row["event"] for row in _events(events)]:
        assert time.monotonic() < deadline, log.read_text()[-4000:]
        assert process.poll() is None, log.read_text()[-4000:]
        time.sleep(0.01)


@pytest.mark.parametrize("mode", ["idle", "inflight", "blocked_disposal", "startup"])
def test_real_server_sigterm_drains_and_disposes_on_owner_thread(tmp_path, shutdown_pki, mode):
    settings = ServiceSettings.from_environment()
    if not (settings.model_dir / "inference.pdiparams").is_file():
        if os.environ.get("LATEX_REC_REQUIRE_MODELS") == "1":
            pytest.fail("Required real shutdown model is missing; run verified prefetch.")
        pytest.skip("Prefetch models before real process shutdown tests.")

    ca, certificate, private_key, client_certificate, client_key = shutdown_pki
    context = ssl.create_default_context(cafile=str(ca))
    context.load_cert_chain(str(client_certificate), str(client_key))
    port = _port()
    events = tmp_path / "events.jsonl"
    release = tmp_path / "release"
    log = tmp_path / "worker.log"
    environment = os.environ.copy()
    environment.update({
        "PYTHONPATH": str(ROOT), "LATEX_REC_MODEL_DIR": str(settings.model_dir),
        "LATEX_REC_HOST": "127.0.0.1", "LATEX_REC_PORT": str(port),
        "LATEX_REC_WORKERS": "1", "LATEX_REC_CPU_THREADS": "1",
        "LATEX_REC_ENVIRONMENT": "production", "LATEX_REC_LOG_LEVEL": "info",
        "LATEX_REC_TLS_CERTIFICATE": str(certificate),
        "LATEX_REC_TLS_PRIVATE_KEY": str(private_key), "LATEX_REC_TLS_CLIENT_CA": str(ca),
        "LATEX_REC_WATCHDOG_SECONDS": "1" if mode == "blocked_disposal" else "55",
        "LATEX_REC_STARTUP_WATCHDOG_SECONDS": "120",
        "OMP_NUM_THREADS": "1", "OPENBLAS_NUM_THREADS": "1", "MKL_NUM_THREADS": "1",
        "NUMEXPR_NUM_THREADS": "1", "TOKENIZERS_PARALLELISM": "false",
        "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK": "True",
    })
    environment.pop("LATEX_REC_EXECUTION_PROVIDER", None)
    endpoint = f"https://127.0.0.1:{port}"
    with log.open("w", encoding="utf-8") as output:
        process = subprocess.Popen(
            [sys.executable, "-c", CHILD, mode, str(events), str(release)],
            cwd=ROOT, env=environment, stdout=output, stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        try:
            with httpx.Client(verify=context, trust_env=False, timeout=15) as client:
                if mode == "startup":
                    _wait_event(process, events, "startup_held", log, timeout=150)
                    # Startup has no listening socket or readiness connection.
                    process.send_signal(signal.SIGTERM)
                    try:
                        time.sleep(0.2)
                        assert process.poll() is None, (
                            "SIGTERM killed native startup before it drained\n" + log.read_text()[-4000:]
                        )
                        assert "close_started" not in [row["event"] for row in _events(events)]
                    finally:
                        release.touch()
                else:
                    _wait_ready(process, client, endpoint, log)
                if mode == "inflight":
                    payload = (ROOT / "tests/corpus/fractions-light.webp").read_bytes()
                    with ThreadPoolExecutor(max_workers=1) as requests:
                        result = requests.submit(client.post, endpoint + "/v2/latex/extract",
                                                 content=payload, headers={"Content-Type": "image/webp"})
                        try:
                            _wait_event(process, events, "extract_started", log, timeout=10)
                            process.send_signal(signal.SIGTERM)
                            time.sleep(0.2)
                            assert process.poll() is None, "SIGTERM killed admitted native work before it drained"
                            assert "close_started" not in [row["event"] for row in _events(events)]
                        finally:
                            release.touch()
                        response = result.result(timeout=20)
                        assert response.status_code == 200, response.text
                        assert response.json()["latex"] == r"\frac{a+b}{c-d}"
                elif mode != "startup":
                    # Idle shutdown has no outstanding client connection. Closing
                    # the TLS readiness probe also completes its close handshake.
                    client.close()
                    process.send_signal(signal.SIGTERM)
                process.wait(timeout=12)
        finally:
            release.touch(exist_ok=True)
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)

    recorded = _events(events)
    assert len({row["thread"] for row in recorded}) == 1, recorded
    topology = json.loads(events.with_name("signals.json").read_text())
    # The supported CLI starts native initialization with only its main thread.
    assert list(topology["before_server"]) == [str(process.pid)], topology
    term_bit = 1 << (signal.SIGTERM - 1)
    assert all(mask & term_bit for mask in topology["native_loaded"].values()), topology
    assert topology["disposal"][str(process.pid)] == topology["before_server"][str(process.pid)], topology
    text = log.read_text()
    assert "FatalError" not in text, text[-4000:]
    if mode == "blocked_disposal":
        assert process.returncode == 70, text[-4000:]
        assert [row["event"] for row in recorded] == ["initialized", "close_started"]
        assert "native_shutdown_watchdog" in text
    else:
        # Uvicorn restores the original handler and replays SIGTERM after cleanup.
        assert process.returncode == -signal.SIGTERM, text[-4000:]
        expected = ["initialized", "close_started", "close_finished"]
        if mode == "inflight":
            expected[1:1] = ["extract_started", "extract_finished"]
        elif mode == "startup":
            expected[1:1] = ["startup_held"]
        assert [row["event"] for row in recorded] == expected, recorded
        assert "worker_stopped" in text and "Application shutdown complete" in text
