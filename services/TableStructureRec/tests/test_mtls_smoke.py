from __future__ import annotations

import os
import io
import json
import shutil
import socket
import ssl
import subprocess
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

import pytest
import uvicorn
from PIL import Image

from table_rec_service.__main__ import uvicorn_options
from table_rec_service.app import create_app
from table_rec_service.config import ServiceSettings


class _ReadyBundle:
    ready = True
    provider_summary: dict[str, list[str]] = {}

    def __init__(self) -> None:
        self.started = threading.Event()
        self.release = threading.Event()

    def extract(self, _image: object) -> object:
        self.started.set()
        self.release.wait(timeout=5)
        return type("Result", (), {"html": "<table><tr><td>ok</td></tr></table>", "table_type": "wired"})()


def _openssl() -> str:
    executable = shutil.which("openssl")
    if executable:
        return executable
    program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
    candidate = program_files / "Git" / "usr" / "bin" / "openssl.exe"
    if candidate.is_file():
        return str(candidate)
    pytest.fail("OpenSSL is required for the production mTLS smoke test")


def _run_openssl(*arguments: str) -> None:
    subprocess.run(
        [_openssl(), *arguments],
        check=True,
        capture_output=True,
        text=True,
    )


def _create_test_pki(directory: Path) -> tuple[Path, Path, Path, Path, Path]:
    ca_key = directory / "ca-key.pem"
    ca = directory / "ca.pem"
    server_key = directory / "server-key.pem"
    server_csr = directory / "server.csr"
    server = directory / "server.pem"
    client_key = directory / "client-key.pem"
    client_csr = directory / "client.csr"
    client = directory / "client.pem"
    server_extensions = directory / "server.ext"
    client_extensions = directory / "client.ext"
    server_extensions.write_text(
        "subjectAltName=IP:127.0.0.1\nextendedKeyUsage=serverAuth\n",
        encoding="ascii",
    )
    client_extensions.write_text("extendedKeyUsage=clientAuth\n", encoding="ascii")

    _run_openssl(
        "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
        "-subj", "/CN=SnowShot Test CA", "-keyout", str(ca_key), "-out", str(ca),
    )
    _run_openssl(
        "req", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=127.0.0.1",
        "-keyout", str(server_key), "-out", str(server_csr),
    )
    _run_openssl(
        "x509", "-req", "-days", "1", "-sha256", "-in", str(server_csr),
        "-CA", str(ca), "-CAkey", str(ca_key), "-CAcreateserial",
        "-extfile", str(server_extensions), "-out", str(server),
    )
    _run_openssl(
        "req", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=snowshot-api",
        "-keyout", str(client_key), "-out", str(client_csr),
    )
    _run_openssl(
        "x509", "-req", "-days", "1", "-sha256", "-in", str(client_csr),
        "-CA", str(ca), "-CAkey", str(ca_key), "-CAserial", str(directory / "ca.srl"),
        "-extfile", str(client_extensions), "-out", str(client),
    )
    return ca, server, server_key, client, client_key


def _available_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def _webp() -> bytes:
    image = io.BytesIO()
    Image.new("RGB", (16, 16), "white").save(image, format="WEBP", lossless=True)
    return image.getvalue()


def test_ready_endpoint_requires_and_accepts_verified_mutual_tls(
    tmp_path: Path,
) -> None:
    ca, server_certificate, server_key, client_certificate, client_key = (
        _create_test_pki(tmp_path)
    )
    port = _available_port()
    settings = ServiceSettings(
        model_dir=tmp_path,
        host="127.0.0.1",
        port=port,
        environment="production",
        tls_certificate=server_certificate,
        tls_private_key=server_key,
        tls_client_ca=ca,
    )
    bundle = _ReadyBundle()
    application = create_app(lambda: bundle)
    config = uvicorn.Config(application, **uvicorn_options(settings))
    service = uvicorn.Server(config)
    thread = threading.Thread(target=service.run, daemon=True)
    thread.start()
    deadline = time.monotonic() + 10
    while not service.started and thread.is_alive() and time.monotonic() < deadline:
        time.sleep(0.05)
    assert service.started

    endpoint = f"https://127.0.0.1:{port}/health/ready"
    try:
        trusted_without_client = ssl.create_default_context(cafile=str(ca))
        with pytest.raises((urllib.error.URLError, ssl.SSLError, ConnectionError)):
            urllib.request.urlopen(endpoint, context=trusted_without_client, timeout=3)

        mutual_tls = ssl.create_default_context(cafile=str(ca))
        mutual_tls.load_cert_chain(str(client_certificate), str(client_key))
        with urllib.request.urlopen(endpoint, context=mutual_tls, timeout=3) as response:
            assert response.status == 200
            assert response.read() == b'{"status":"ready"}'

        body = _webp()
        extraction = urllib.request.Request(
            f"https://127.0.0.1:{port}/v2/table/extract",
            data=body,
            headers={"Content-Type": "image/webp"},
            method="POST",
        )
        first_result: list[int] = []

        def run_first() -> None:
            with urllib.request.urlopen(extraction, context=mutual_tls, timeout=5) as response:
                first_result.append(response.status)

        first = threading.Thread(target=run_first)
        first.start()
        assert bundle.started.wait(timeout=3)
        with pytest.raises(urllib.error.HTTPError) as busy:
            urllib.request.urlopen(extraction, context=mutual_tls, timeout=3)
        assert busy.value.code == 503
        assert json.loads(busy.value.read())["error"]["code"] == "worker_busy"
        bundle.release.set()
        first.join(timeout=5)
        assert first_result == [200]
    finally:
        service.should_exit = True
        thread.join(timeout=10)
    assert not thread.is_alive()
