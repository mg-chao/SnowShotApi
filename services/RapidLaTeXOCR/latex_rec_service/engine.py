from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from .errors import NO_FORMULA
from .model_manifest import verify_models


@dataclass(frozen=True)
class ExtractionResult:
    latex: str


class EngineBundle:
    def __init__(self, model, provider_summary):
        self.model = model
        self.provider_summary = provider_summary
        self.ready = True

    @classmethod
    def load(
        cls,
        model_dir: Path,
        execution_provider: str = "DmlExecutionProvider",
        cpu_threads: int = 0,
    ) -> "EngineBundle":
        if execution_provider not in {"DmlExecutionProvider", "CPUExecutionProvider"}:
            raise ValueError(f"Unsupported execution provider: {execution_provider}")
        if not 0 <= cpu_threads <= min(256, os.cpu_count() or 256):
            raise ValueError("cpu_threads must not exceed the host logical CPU count")
        if execution_provider == "CPUExecutionProvider":
            import cv2

            cv2.setNumThreads(1)
        verify_models(model_dir, Path(__file__).resolve().parents[1] / "model-manifest.json")
        from rapid_latex_ocr import LaTeXOCR

        model = LaTeXOCR(
            image_resizer_path=model_dir / "image_resizer.onnx",
            encoder_path=model_dir / "encoder.onnx",
            decoder_path=model_dir / "decoder.onnx",
            tokenizer_json=model_dir / "tokenizer.json",
            execution_provider=execution_provider,
            num_threads=cpu_threads if execution_provider == "CPUExecutionProvider" else -1,
        )
        sessions = {
            "resizer": model.image_resizer.session,
            "encoder": model.encoder_decoder.encoder.session,
            "decoder": model.encoder_decoder.decoder.session.session,
        }
        summary = {name: session.get_providers() for name, session in sessions.items()}
        for name, providers in summary.items():
            if not providers or providers[0] != execution_provider:
                raise RuntimeError(f"LaTeX {name} did not initialize with {execution_provider}: {providers}")
            if execution_provider == "CPUExecutionProvider" and providers != [execution_provider]:
                raise RuntimeError(f"LaTeX {name} did not initialize with CPU-only inference: {providers}")
        bundle = cls(model, summary)
        # Execute all three models before advertising readiness, including the autoregressive decoder.
        image = Image.new("RGB", (128, 64), "white")
        ImageDraw.Draw(image).text((12, 12), "x = 1", fill="black", font_size=24)
        bundle.extract(np.asarray(image))
        return bundle

    def extract(self, image: np.ndarray) -> ExtractionResult:
        # Upstream normalization divides by the intensity range; blank images have no range.
        gray = np.asarray(Image.fromarray(image).convert("L"))
        if int(gray.max()) == int(gray.min()):
            raise NO_FORMULA.exception()
        latex, _elapsed = self.model(image)
        if not isinstance(latex, str) or not latex.strip():
            raise NO_FORMULA.exception()
        return ExtractionResult(latex.strip())
