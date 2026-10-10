from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from latex_rec_service.app import create_app
from latex_rec_service.config import ServiceSettings
from latex_rec_service.engine import EngineBundle
from tests.corpus_support import matches_expected

ROOT = Path(__file__).resolve().parents[1]
CORPUS = ROOT / "tests/corpus"
FIXTURES = json.loads((CORPUS / "manifest.json").read_text()) if (CORPUS / "manifest.json").is_file() else []
pytestmark = [
    pytest.mark.recognition,
    # This optional build-cache warning has no bearing on static CPU inference.
    pytest.mark.filterwarnings("ignore:No ccache found.*:UserWarning:paddle.utils.cpp_extension.extension_utils"),
]


class FormulaMismatchError(AssertionError):
    """Only a verified HTTP success with incorrect math is an expected limitation."""


@pytest.fixture(scope="module")
def recognition_client():
    settings = ServiceSettings.from_environment()
    if not (settings.model_dir / "inference.pdiparams").is_file():
        if os.environ.get("LATEX_REC_REQUIRE_MODELS") == "1":
            pytest.fail("Required real recognition model is missing; run verified prefetch.")
        pytest.skip("Prefetch models before real recognition tests.")
    if not FIXTURES:
        pytest.fail("The committed independent recognition corpus is missing.")
    with TestClient(create_app(lambda: EngineBundle.load(settings.model_dir, settings.cpu_threads))) as client:
        yield client


@pytest.mark.parametrize("fixture", [
    pytest.param(item, id=item["id"], marks=pytest.mark.xfail(strict=True, raises=FormulaMismatchError, reason=item["known_limitation"]))
    if item.get("known_limitation") else pytest.param(item, id=item["id"])
    for item in FIXTURES
])
def test_independent_formula_through_http(recognition_client, fixture):
    payload = (CORPUS / fixture["image"]).read_bytes()
    assert hashlib.sha256(payload).hexdigest() == fixture["sha256"]
    response = recognition_client.post("/v2/latex/extract", content=payload,
                                       headers={"Content-Type": "image/webp"})
    assert response.status_code == 200, response.json()
    if not matches_expected(response.json()["latex"], fixture):
        raise FormulaMismatchError(response.json()["latex"])


def test_runtime_cpu_configuration(recognition_client):
    bundle = recognition_client.app.state.engine_bundle
    summary = bundle.runtime_summary
    assert summary["device"] == "cpu"
    assert summary["cpu_threads"] == ServiceSettings.from_environment().cpu_threads
    assert summary["model"] == "PP-FormulaNet_plus-S"


def test_real_paddle_startup_with_network_disabled(monkeypatch):
    settings = ServiceSettings.from_environment()
    if not (settings.model_dir / "inference.pdiparams").is_file():
        if os.environ.get("LATEX_REC_REQUIRE_MODELS") == "1":
            pytest.fail("Required recognition model is missing.")
        pytest.skip("Prefetch models before offline recognition tests.")
    import socket

    def no_network(*_args, **_kwargs):
        raise AssertionError("Model startup attempted network access")

    monkeypatch.setattr(socket.socket, "connect", no_network)
    with TestClient(create_app()) as client:
        assert client.get("/health/ready").status_code == 200
