from __future__ import annotations

import os
import threading
from collections.abc import Mapping
from dataclasses import dataclass
from importlib.metadata import version
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from .errors import INFERENCE_FAILED, NO_FORMULA
from .model_manifest import MODEL_NAME, verify_models

MAX_RAW_TOKENS = 1026  # Three BOS tokens + 341 parallel steps of three tokens.
VOCABULARY_SIZE = 50000
BOS_PREFIX = (0, 0, 0)
EOS_TOKEN = 2


@dataclass(frozen=True)
class ExtractionResult:
    latex: str


class CompletionCheckedDecoder:
    """Validate the pinned model's raw sequence before the official decoder loses EOS.

    PaddleX 3.7.2 calls post_op with one flattened integer sequence per image.
    This is the only adaptation of its internal predictor interface. Preprocessing,
    native inference and text decoding stay upstream; compatibility is tested.
    """

    def __init__(self, decoder):
        self.decoder = decoder

    def __call__(self, predictions):
        if not isinstance(predictions, (list, tuple)) or len(predictions) != 1:
            raise INFERENCE_FAILED.exception()
        try:
            tokens = np.asarray(predictions[0])
        except (TypeError, ValueError) as exception:
            raise INFERENCE_FAILED.exception() from exception
        if (
            tokens.ndim != 1
            or not np.issubdtype(tokens.dtype, np.integer)
            or not 6 <= tokens.size <= MAX_RAW_TOKENS
            or tokens.size % 3 != 0
            or tuple(tokens[:3]) != BOS_PREFIX
            or np.any(tokens < 0)
            or np.any(tokens >= VOCABULARY_SIZE)
            or not np.any(tokens[3:] == EOS_TOKEN)
        ):
            raise INFERENCE_FAILED.exception()
        # A parallel step can emit further tokens after EOS; the upstream decoder
        # truncates at the first EOS and must retain that responsibility.
        decoded = self.decoder(predictions)
        if not isinstance(decoded, list) or len(decoded) != 1 or not isinstance(decoded[0], str):
            raise INFERENCE_FAILED.exception()
        return decoded


def normalize_polarity(image: np.ndarray) -> np.ndarray:
    """Convert dark screenshot backgrounds once, preserving antialiasing and color."""
    if image.dtype != np.uint8 or image.ndim != 3 or image.shape[2] != 3 or 0 in image.shape[:2]:
        raise ValueError("Formula inference requires a nonempty uint8 RGB image.")
    gray = np.asarray(Image.fromarray(image).convert("L"))
    if int(gray.max()) == int(gray.min()):
        raise NO_FORMULA.exception()
    # Sample each perimeter pixel once; duplicate corners otherwise bias tiny crops.
    if gray.shape[0] == 1:
        border = gray[0]
    elif gray.shape[1] == 1:
        border = gray[:, 0]
    else:
        border = np.concatenate((gray[0], gray[-1], gray[1:-1, 0], gray[1:-1, -1]))
    return np.ascontiguousarray(255 - image if np.median(border) < 128 else image)


class EngineBundle:
    def __init__(self, model, runtime_summary):
        self.model = model
        self.runtime_summary = runtime_summary
        self.ready = True
        self._owner_thread = threading.get_ident()

    def _check_owner(self) -> None:
        if threading.get_ident() != self._owner_thread:
            raise RuntimeError("Formula predictor must remain on its owner thread.")
        if self.model is None:
            raise RuntimeError("Formula predictor is closed.")

    @classmethod
    def load(cls, model_dir: Path, cpu_threads: int = 1) -> "EngineBundle":
        cpu_limit = os.cpu_count() or 1
        if hasattr(os, "sched_getaffinity"):
            cpu_limit = min(cpu_limit, len(os.sched_getaffinity(0)))
        if type(cpu_threads) is not int or not 1 <= cpu_threads <= min(256, cpu_limit):
            raise ValueError("cpu_threads must be positive and not exceed the available logical CPU count")
        verify_models(model_dir)
        # Set before importing PaddleX: local startup must never discover/download models.
        os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
        os.environ["TOKENIZERS_PARALLELISM"] = "false"
        import cv2
        from paddlex import create_predictor

        cv2.setNumThreads(1)
        model = create_predictor(
            model_name=MODEL_NAME,
            model_dir=str(model_dir),
            device="cpu",
            engine="paddle_static",
            engine_config={"run_mode": "paddle", "cpu_threads": cpu_threads},
            batch_size=1,
        )
        bundle = None
        try:
            config = model.runner._config
            if (
                config.get("device_type") != "cpu"
                or config.get("run_mode") != "paddle"
                or config.get("cpu_threads") != cpu_threads
            ):
                raise RuntimeError("Formula predictor did not initialize with the fixed CPU configuration.")
            model.post_op = CompletionCheckedDecoder(model.post_op)
            summary = {
                "model": MODEL_NAME,
                "engine": "paddle_static",
                "device": "cpu",
                "cpu_threads": cpu_threads,
                "mkldnn": False,
                "paddle_version": version("paddlepaddle"),
                "paddlex_version": version("paddlex"),
            }
            bundle = cls(model, summary)
            bundle.ready = False
            image = Image.new("RGB", (128, 64), "white")
            ImageDraw.Draw(image).text((12, 12), "x = 1", fill="black", font_size=24)
            bundle.extract(np.asarray(image))
            bundle.ready = True
            return bundle
        except BaseException:
            if bundle is not None:
                bundle.close()
            else:
                model.close()
            raise

    def extract(self, image: np.ndarray) -> ExtractionResult:
        self._check_owner()
        normalized = normalize_polarity(image)
        # PaddleX ndarray inputs are BGR even though its formula reader targets RGB.
        result = list(self.model.predict(np.ascontiguousarray(normalized[:, :, ::-1]), batch_size=1))
        if len(result) != 1 or not isinstance(result[0], Mapping):
            raise INFERENCE_FAILED.exception()
        latex = result[0].get("rec_formula")
        if not isinstance(latex, str):
            raise INFERENCE_FAILED.exception()
        if not latex.strip():
            raise NO_FORMULA.exception()
        return ExtractionResult(latex.strip())

    def close(self) -> None:
        if threading.get_ident() != self._owner_thread:
            raise RuntimeError("Formula predictor must be closed on its owner thread.")
        self.ready = False
        model, self.model = self.model, None
        if model is not None:
            model.close()
