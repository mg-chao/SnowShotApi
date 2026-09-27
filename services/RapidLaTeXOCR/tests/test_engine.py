from pathlib import Path
import io

import numpy as np
import pytest
from PIL import Image

from latex_rec_service.engine import EngineBundle
from latex_rec_service.errors import ServiceError
from latex_rec_service.image_validation import decode_webp


def test_blank_input_does_not_invoke_model():
    def forbidden(_):
        pytest.fail("Blank image reached inference")
    bundle = EngineBundle(forbidden, {})
    with pytest.raises(ServiceError, match="no_formula"):
        bundle.extract(np.full((10, 10, 3), 255, dtype=np.uint8))


def test_empty_output_is_not_a_success():
    bundle = EngineBundle(lambda _: (" ", 0), {})
    image = np.full((10, 10, 3), 255, dtype=np.uint8)
    image[2, 2] = 0
    with pytest.raises(ServiceError, match="no_formula"):
        bundle.extract(image)


def test_transparency_is_composited_on_white():
    image = Image.new("RGBA", (8, 8), (0, 0, 0, 0))
    image.putpixel((2, 2), (0, 0, 0, 255))
    output = io.BytesIO()
    image.save(output, format="WEBP", lossless=True)
    decoded = decode_webp(output.getvalue())
    assert decoded[0, 0].tolist() == [255, 255, 255]
    assert decoded[2, 2].tolist() == [0, 0, 0]


def test_animated_webp_is_rejected():
    output = io.BytesIO()
    Image.new("RGB", (8, 8), "white").save(output, format="WEBP", save_all=True,
        append_images=[Image.new("RGB", (8, 8), "black")], duration=100, loop=0)
    with pytest.raises(ServiceError, match="invalid_image"):
        decode_webp(output.getvalue())


def test_missing_models_fail_before_session_creation(tmp_path: Path):
    with pytest.raises(ValueError, match="hash verification failed"):
        EngineBundle.load(tmp_path)


def test_missing_directml_fails_closed(monkeypatch, tmp_path):
    import rapid_latex_ocr.utils_load as upstream
    model = tmp_path / "model.onnx"
    model.write_bytes(b"unused")
    monkeypatch.setattr(upstream, "get_available_providers", lambda: ["CPUExecutionProvider"])
    with pytest.raises(RuntimeError, match="DirectML is required"):
        upstream.OrtInferSession(model)
