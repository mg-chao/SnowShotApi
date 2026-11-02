from __future__ import annotations

import io
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
from PIL import Image

from latex_rec_service import engine
from latex_rec_service.engine import CompletionCheckedDecoder, EngineBundle, MAX_RAW_TOKENS, normalize_polarity
from latex_rec_service.errors import ServiceError
from latex_rec_service.image_validation import decode_webp


def image():
    result = np.full((10, 10, 3), 255, dtype=np.uint8)
    result[2, 2] = [10, 20, 30]
    return result


class Predictor:
    def __init__(self, output="x = 1", config=None):
        self.output = output
        self.inputs = []
        self.closed = False
        self.post_op = lambda _: [self.output]
        self.runner = SimpleNamespace(_config=config or {"device_type": "cpu", "run_mode": "paddle", "cpu_threads": 1})

    def predict(self, input, batch_size):
        assert batch_size == 1
        self.inputs.append(input.copy())
        decoded = self.post_op([np.array([0, 0, 0, 10, 2, 1], dtype=np.int64)])
        yield {"rec_formula": decoded[0]}

    def close(self):
        self.closed = True


@pytest.mark.parametrize("value", [0, 128, 255])
def test_blank_input_does_not_invoke_model(value):
    model = Predictor()
    with pytest.raises(ServiceError, match="no_formula"):
        EngineBundle(model, {}).extract(np.full((10, 10, 3), value, dtype=np.uint8))
    assert not model.inputs


def test_empty_output_is_not_a_success():
    with pytest.raises(ServiceError, match="no_formula"):
        EngineBundle(Predictor(" \n "), {}).extract(image())


def test_latex_content_is_preserved_and_rgb_is_sent_as_contiguous_bgr():
    formula = r"\begin{aligned}a&=b\\c&=\text{ a b }\end{aligned}"
    model = Predictor(" \n" + formula + "\n ")
    assert EngineBundle(model, {}).extract(image()).latex == formula
    assert model.inputs[0][2, 2].tolist() == [30, 20, 10]
    assert model.inputs[0].flags.c_contiguous


def test_light_and_dark_screenshots_reach_identical_pixels():
    model = Predictor()
    bundle = EngineBundle(model, {})
    light = image()
    bundle.extract(light)
    bundle.extract(255 - light)
    np.testing.assert_array_equal(model.inputs[0], model.inputs[1])
    assert light[2, 2].tolist() == [10, 20, 30]


def test_polarity_threshold_preserves_midgray_background():
    sample = np.full((4, 4, 3), 128, dtype=np.uint8)
    sample[1, 1] = 0
    np.testing.assert_array_equal(normalize_polarity(sample), sample)


@pytest.mark.parametrize("shape", [(1, 4, 3), (4, 1, 3), (2, 2, 3)])
def test_tiny_crops_have_a_valid_perimeter(shape):
    sample = np.full(shape, 255, dtype=np.uint8)
    sample.reshape(-1, 3)[0] = 0
    assert normalize_polarity(sample).shape == shape


def test_complete_parallel_output_accepts_tokens_after_eos():
    tokens = np.array([0, 0, 0, 10, 2, 49000])
    seen = []
    decoder = CompletionCheckedDecoder(lambda predictions: seen.append(predictions) or ["x"])
    assert decoder([tokens]) == ["x"]
    assert seen[0][0] is tokens


def test_complete_sequence_at_model_cap_is_a_success():
    tokens = np.full(MAX_RAW_TOKENS, 10, dtype=np.int64)
    tokens[:3], tokens[-1] = 0, 2
    assert CompletionCheckedDecoder(lambda _: ["long formula"])([tokens]) == ["long formula"]


def test_capped_incomplete_sequence_is_not_decoded():
    tokens = np.full(MAX_RAW_TOKENS, 10, dtype=np.int64)
    tokens[:3] = 0
    with pytest.raises(ServiceError, match="inference_failed"):
        CompletionCheckedDecoder(lambda _: pytest.fail("Incomplete sequence reached decoder"))([tokens])


@pytest.mark.parametrize("tokens", [
    np.array([], dtype=np.int64),
    np.array([0, 0, 0, 10, 10, 10]),
    np.array([0, 0, 0, 10, 2]),
    np.array([0, 0, 0, 10, 2, 1], dtype=np.float32),
    np.array([[0, 0, 0, 10, 2, 1]]),
    np.array([1, 0, 0, 10, 2, 1]),
    np.array([0, 0, 0, -1, 2, 1]),
    np.array([0, 0, 0, 50000, 2, 1]),
    np.array([0, 0, 0, 10, 2, None], dtype=object),
])
def test_malformed_tokens_fail_closed(tokens):
    with pytest.raises(ServiceError, match="inference_failed"):
        CompletionCheckedDecoder(lambda _: ["x"])([tokens])


@pytest.mark.parametrize("decoded", [None, [], ["x", "y"], [None], "x"])
def test_malformed_decoder_result_fails_closed(decoded):
    with pytest.raises(ServiceError, match="inference_failed"):
        CompletionCheckedDecoder(lambda _: decoded)([np.array([0, 0, 0, 10, 2, 1])])


def test_model_calls_and_close_remain_on_owner_thread():
    model = Predictor()
    bundle = EngineBundle(model, {})
    with ThreadPoolExecutor(max_workers=1) as executor:
        with pytest.raises(RuntimeError, match="owner thread"):
            executor.submit(bundle.extract, image()).result()
        with pytest.raises(RuntimeError, match="owner thread"):
            executor.submit(bundle.close).result()
    bundle.close()
    bundle.close()
    assert model.closed and bundle.model is None and not bundle.ready
    with pytest.raises(RuntimeError, match="closed"):
        bundle.extract(image())


def mock_loader(monkeypatch, model):
    calls = {}
    monkeypatch.setattr(engine, "verify_models", lambda path: calls.update(verified=path))
    monkeypatch.setattr(engine, "version", lambda package: {"paddlepaddle": "3.3.1", "paddlex": "3.7.2"}[package])
    monkeypatch.setitem(sys.modules, "cv2", SimpleNamespace(setNumThreads=lambda n: calls.update(opencv_threads=n)))

    def create_predictor(**kwargs):
        assert engine.os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] == "True"
        calls.update(kwargs)
        return model

    monkeypatch.setitem(sys.modules, "paddlex", SimpleNamespace(create_predictor=create_predictor))
    return calls


def test_load_verifies_assets_and_warms_fixed_cpu_runtime(monkeypatch, tmp_path):
    model = Predictor()
    calls = mock_loader(monkeypatch, model)
    bundle = EngineBundle.load(tmp_path)
    assert calls["verified"] == tmp_path
    assert calls["device"] == "cpu" and calls["engine"] == "paddle_static"
    assert calls["model_dir"] == str(tmp_path)
    assert calls["engine_config"] == {"run_mode": "paddle", "cpu_threads": 1}
    assert calls["opencv_threads"] == 1 and calls["batch_size"] == 1
    assert isinstance(model.post_op, CompletionCheckedDecoder)
    assert len(model.inputs) == 1 and bundle.ready
    assert bundle.runtime_summary == {
        "model": "PP-FormulaNet_plus-S", "engine": "paddle_static", "device": "cpu",
        "cpu_threads": 1, "mkldnn": False, "paddle_version": "3.3.1", "paddlex_version": "3.7.2",
    }
    bundle.close()


@pytest.mark.parametrize("config", [
    {"device_type": "gpu", "run_mode": "paddle", "cpu_threads": 1},
    {"device_type": "cpu", "run_mode": "mkldnn", "cpu_threads": 1},
    {"device_type": "cpu", "run_mode": "paddle", "cpu_threads": 2},
])
def test_load_rejects_runtime_fallback(monkeypatch, tmp_path, config):
    model = Predictor(config=config)
    mock_loader(monkeypatch, model)
    with pytest.raises(RuntimeError, match="fixed CPU configuration"):
        EngineBundle.load(tmp_path)
    assert model.closed


def test_warmup_failure_disposes_model(monkeypatch, tmp_path):
    model = Predictor(output="")
    mock_loader(monkeypatch, model)
    with pytest.raises(ServiceError, match="no_formula"):
        EngineBundle.load(tmp_path)
    assert model.closed


def test_missing_models_fail_before_runtime_import(tmp_path: Path):
    with pytest.raises(ValueError, match="hash verification failed"):
        EngineBundle.load(tmp_path)


@pytest.mark.parametrize("threads", [0, -1, True, 1.5, 257])
def test_invalid_thread_counts_fail_before_model_load(tmp_path, threads):
    with pytest.raises(ValueError, match="cpu_threads"):
        EngineBundle.load(tmp_path, threads)


def test_thread_count_respects_linux_affinity(monkeypatch, tmp_path):
    monkeypatch.setattr(engine.os, "cpu_count", lambda: 32)
    monkeypatch.setattr(engine.os, "sched_getaffinity", lambda _: {0, 2}, raising=False)
    with pytest.raises(ValueError, match="available logical CPU"):
        EngineBundle.load(tmp_path, 3)


def test_transparency_is_composited_on_white():
    sample = Image.new("RGBA", (8, 8), (0, 0, 0, 0))
    sample.putpixel((2, 2), (0, 0, 0, 255))
    output = io.BytesIO()
    sample.save(output, format="WEBP", lossless=True)
    decoded = decode_webp(output.getvalue())
    assert decoded[0, 0].tolist() == [255, 255, 255]
    assert decoded[2, 2].tolist() == [0, 0, 0]


def test_animated_webp_is_rejected():
    output = io.BytesIO()
    Image.new("RGB", (8, 8), "white").save(output, format="WEBP", save_all=True,
        append_images=[Image.new("RGB", (8, 8), "black")], duration=100, loop=0)
    with pytest.raises(ServiceError, match="invalid_image"):
        decode_webp(output.getvalue())
