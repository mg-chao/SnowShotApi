from __future__ import annotations

import io
import json
import threading
import time
from dataclasses import dataclass
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from table_rec_service.app import create_app
from table_rec_service.engine import ExtractionResult
from table_rec_service.errors import NO_TABLE


def image_bytes(image_format: str = "WEBP", size: tuple[int, int] = (8, 8)) -> bytes:
    output = io.BytesIO()
    Image.new("RGB", size, "white").save(output, format=image_format, lossless=True)
    return output.getvalue()


@dataclass
class FakeBundle:
    result_html: str = "<html><body><table><tr><td>x</td></tr></table></body></html>"
    error: Exception | None = None
    ready: bool = True

    def __post_init__(self) -> None:
        self.provider_summary = {
            "classifier": ["DmlExecutionProvider"], "ocr": ["DmlExecutionProvider"],
            "wired": ["DmlExecutionProvider"], "lineless": ["DmlExecutionProvider"],
        }

    def extract(self, _image) -> ExtractionResult:
        if self.error:
            raise self.error
        return ExtractionResult(self.result_html, "wired")


def client_for(bundle: FakeBundle) -> TestClient:
    return TestClient(create_app(lambda: bundle))


def post_image(client: TestClient, payload: bytes | None = None, **kwargs):
    headers = {"Content-Type": "image/webp", **kwargs.pop("headers", {})}
    return client.post("/v2/table/extract", content=image_bytes() if payload is None else payload,
                       headers=headers, **kwargs)


def error_code(response) -> str:
    return response.json()["error"]["code"]


def test_raw_webp_success_and_v1_is_rejected() -> None:
    with client_for(FakeBundle()) as client:
        success = post_image(client)
        old_contract = client.post("/v1/table/extract", content=image_bytes(),
                                   headers={"Content-Type": "image/webp"})
    assert success.status_code == 200
    assert success.json()["html"].startswith("<html>")
    assert old_contract.status_code == 404


def test_media_type_empty_body_and_payload_signature_are_validated() -> None:
    with client_for(FakeBundle()) as client:
        wrong_media = client.post("/v2/table/extract", content=image_bytes(),
                                  headers={"Content-Type": "application/octet-stream"})
        empty = post_image(client, b"")
        png = post_image(client, image_bytes("PNG"))
    assert wrong_media.status_code == 415
    assert error_code(wrong_media) == "not_webp"
    assert empty.status_code == 422
    assert error_code(empty) == "invalid_image"
    assert png.status_code == 415
    assert error_code(png) == "not_webp"


def test_corrupt_webp_is_invalid_image() -> None:
    corrupt = b"RIFF" + (8).to_bytes(4, "little") + b"WEBPbroken"
    with client_for(FakeBundle()) as client:
        response = post_image(client, corrupt)
    assert response.status_code == 422
    assert error_code(response) == "invalid_image"


def test_image_dimension_limit_boundary() -> None:
    with client_for(FakeBundle()) as client:
        exact = post_image(client, image_bytes(size=(2880, 2880)))
        width_over = post_image(client, image_bytes(size=(2881, 1)))
        height_over = post_image(client, image_bytes(size=(1, 2881)))
    assert exact.status_code == 200
    assert width_over.status_code == 422 and error_code(width_over) == "image_too_large"
    assert height_over.status_code == 422 and error_code(height_over) == "image_too_large"


@pytest.mark.parametrize(
    ("bundle", "status_code", "code"),
    [(FakeBundle(error=NO_TABLE.exception()), 422, "no_table"),
     (FakeBundle(error=RuntimeError("native failure")), 500, "inference_failed"),
     (FakeBundle(result_html=""), 422, "no_table")],
)
def test_engine_outcomes_have_stable_errors(bundle: FakeBundle, status_code: int, code: str) -> None:
    with client_for(bundle) as client:
        response = post_image(client)
    assert response.status_code == status_code
    assert error_code(response) == code


def test_health_readiness_and_unready_extraction() -> None:
    with client_for(FakeBundle()) as client:
        assert client.get("/health/live").json() == {"status": "live"}
        assert client.get("/health/ready").json() == {"status": "ready"}
    with client_for(FakeBundle(ready=False)) as client:
        assert client.get("/health/ready").status_code == 503
        extract = post_image(client)
    assert extract.status_code == 503
    assert error_code(extract) == "worker_unavailable"


def test_failed_startup_releases_inference_executor(monkeypatch: pytest.MonkeyPatch) -> None:
    import table_rec_service.app as service_app
    shutdown: list[bool] = []

    class FakeGate:
        def __init__(self, *_args, **_kwargs) -> None: pass
        def shutdown(self) -> None: shutdown.append(True)

    monkeypatch.setattr(service_app, "InferenceGate", FakeGate)
    with pytest.raises(RuntimeError, match="model load failed"):
        with TestClient(service_app.create_app(lambda: (_ for _ in ()).throw(RuntimeError("model load failed")))):
            pass
    assert shutdown == [True]


def test_worker_rejects_concurrent_inference_without_queueing() -> None:
    class ConcurrencyBundle(FakeBundle):
        def __post_init__(self) -> None:
            super().__post_init__(); self.guard = threading.Lock(); self.active = 0; self.maximum_active = 0
        def extract(self, image) -> ExtractionResult:
            with self.guard:
                self.active += 1; self.maximum_active = max(self.maximum_active, self.active)
            time.sleep(0.05)
            try: return super().extract(image)
            finally:
                with self.guard: self.active -= 1

    bundle = ConcurrencyBundle()
    with client_for(bundle) as client:
        responses: list[int] = []
        threads = [threading.Thread(target=lambda: responses.append(post_image(client).status_code)) for _ in range(4)]
        for thread in threads: thread.start()
        for thread in threads: thread.join()
    assert responses.count(200) == 1
    assert responses.count(503) == 3
    assert bundle.maximum_active == 1


def test_generated_html_is_returned_unchanged() -> None:
    html = (
        '<html><body><table onclick="alert(1)"><tr><td rowspan="2">'
        'safe<script>alert(1)</script>&lt;value&gt;</td></tr></table></body></html>'
    )
    bundle = FakeBundle(result_html=html)
    with client_for(bundle) as client:
        response = post_image(client)
    assert response.status_code == 200
    assert response.json()["html"] == html


def test_declared_and_streamed_oversize_bodies_are_rejected(monkeypatch: pytest.MonkeyPatch) -> None:
    import table_rec_service.app as service_app
    monkeypatch.setattr(service_app, "MAX_UPLOAD_BYTES", 32)
    with client_for(FakeBundle()) as client:
        declared = post_image(client, b"unused", headers={"Content-Length": "33"})
        streamed = client.post("/v2/table/extract", content=iter([b"RIFF", b"x" * 40]),
                               headers={"Content-Type": "image/webp"})
    assert declared.status_code == 413 and error_code(declared) == "payload_too_large"
    assert streamed.status_code == 413 and error_code(streamed) == "payload_too_large"


def test_generated_openapi_matches_committed_private_contract() -> None:
    generated = create_app(lambda: FakeBundle()).openapi()
    committed = json.loads((Path(__file__).parents[1] / "openapi.private.json").read_text(encoding="utf-8"))
    extract = generated["paths"]["/v2/table/extract"]["post"]
    projected = {
        "openapi": generated["openapi"],
        "paths": {
            "/health/live": {"get": {"statuses": sorted(generated["paths"]["/health/live"]["get"]["responses"])}},
            "/health/ready": {"get": {"statuses": sorted(generated["paths"]["/health/ready"]["get"]["responses"])}},
            "/v2/table/extract": {"post": {
                "headers": sorted(parameter["name"] for parameter in extract["parameters"]),
                "contentType": next(iter(extract["requestBody"]["content"])),
                "successSchema": _schema_name(extract["responses"]["200"]["content"]["application/json"]["schema"]),
                "errorSchema": _schema_name(extract["responses"]["415"]["content"]["application/json"]["schema"]),
                "statuses": sorted(extract["responses"]),
            }},
        },
    }
    assert projected == committed


def _schema_name(schema: dict[str, str]) -> str:
    return schema["$ref"].rsplit("/", maxsplit=1)[-1]
