from pathlib import Path
import io

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from latex_rec_service.app import create_app
from latex_rec_service.engine import EngineBundle

ROOT = Path(__file__).resolve().parents[1]
pytestmark = pytest.mark.skipif(
    not (ROOT / "models/image_resizer.onnx").is_file(),
    reason="Prefetch models on a DirectML-capable Windows machine for real recognition tests.",
)


@pytest.fixture(scope="module")
def bundle():
    return EngineBundle.load(ROOT / "models")


@pytest.mark.parametrize("filename,expected", [
    ("2.png", r"x^{2}+y^{2}=1"),
    ("6.png", r"{\frac{x^{2}}{a^{2}}}-{\frac{y^{2}}{b^{2}}}=1"),
])
def test_real_directml_formula_through_http(bundle, filename, expected):
    for providers in bundle.provider_summary.values():
        assert providers[0] == "DmlExecutionProvider"
    payload = io.BytesIO()
    with Image.open(ROOT / "tests/test_files" / filename) as image:
        image.save(payload, format="WEBP", lossless=True)
    with TestClient(create_app(lambda: bundle)) as client:
        response = client.post("/v2/latex/extract", content=payload.getvalue(),
            headers={"Content-Type": "image/webp"})
    assert response.status_code == 200
    assert response.json()["latex"] == expected
