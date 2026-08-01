from __future__ import annotations

import logging
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np

from .errors import INFERENCE_FAILED, NO_TABLE, ServiceError
from .model_manifest import verify_models

LOGGER = logging.getLogger("table_rec_service.engine")

RAPIDOCR_MODEL_NAMES = (
    "PP-OCRv6_det_small.onnx",
    "ch_ppocr_mobile_v2.0_cls_mobile.onnx",
    "PP-OCRv6_rec_small.onnx",
)
TABLE_MODEL_PATHS = {
    "classifier": Path("table_cls/yolo_cls.onnx"),
    "wired": Path("wired/unet.onnx"),
    "lineless_detect": Path("lineless/detect.onnx"),
    "lineless_process": Path("lineless/process.onnx"),
}


@dataclass(frozen=True)
class ExtractionResult:
    html: str
    table_type: str


def build_table_ocr_result(ocr_output: Any) -> list[list[Any]]:
    boxes = getattr(ocr_output, "boxes", None)
    texts = getattr(ocr_output, "txts", None)
    scores = getattr(ocr_output, "scores", None)
    if boxes is None or texts is None or scores is None:
        return []

    return [
        [box, str(text), float(score)]
        for box, text, score in zip(boxes, texts, scores, strict=True)
    ]


class EngineBundle:
    def __init__(
        self,
        classifier: Any,
        ocr: Any,
        wired: Any,
        lineless: Any,
        provider_summary: dict[str, list[str]],
    ) -> None:
        self.classifier = classifier
        self.ocr = ocr
        self.wired = wired
        self.lineless = lineless
        self.provider_summary = provider_summary
        self.ready = True

    @classmethod
    def load(cls, model_dir: Path) -> "EngineBundle":
        model_dir = model_dir.resolve()
        verify_models(
            model_dir,
            Path(__file__).resolve().parents[1] / "model-manifest.json",
        )
        cls._require_models(model_dir)

        from rapidocr import RapidOCR
        from table_cls import TableCls
        from wired_table_rec import WiredTableRecognition
        from wired_table_rec.main import WiredTableInput
        from lineless_table_rec import LinelessTableRecognition
        from lineless_table_rec.main import LinelessTableInput

        rapidocr_dir = model_dir / "rapidocr"
        ocr = RapidOCR(
            params={
                "Global.return_word_box": True,
                "EngineConfig.onnxruntime.use_dml": True,
                "Det.model_path": str(rapidocr_dir / RAPIDOCR_MODEL_NAMES[0]),
                "Cls.model_path": str(rapidocr_dir / RAPIDOCR_MODEL_NAMES[1]),
                "Rec.model_path": str(rapidocr_dir / RAPIDOCR_MODEL_NAMES[2]),
            }
        )
        classifier = TableCls(
            model_path=str(model_dir / TABLE_MODEL_PATHS["classifier"]),
            use_dml=True,
        )
        wired = WiredTableRecognition(
            WiredTableInput(
                model_path=str(model_dir / TABLE_MODEL_PATHS["wired"]),
                use_dml=True,
            )
        )
        lineless = LinelessTableRecognition(
            LinelessTableInput(
                model_path={
                    "lore_detect": str(
                        model_dir / TABLE_MODEL_PATHS["lineless_detect"]
                    ),
                    "lore_process": str(
                        model_dir / TABLE_MODEL_PATHS["lineless_process"]
                    ),
                },
                use_dml=True,
            )
        )

        providers = {
            "classifier": _providers(classifier.table_engine.table_cls),
            "ocr_det": _providers(ocr.text_det),
            "ocr_cls": _providers(ocr.text_cls),
            "ocr_rec": _providers(ocr.text_rec),
            "wired": _providers(wired.table_structure.session),
            "lineless_detect": _providers(lineless.table_structure.det_session),
            "lineless_process": _providers(lineless.table_structure.process_session),
        }
        for component in providers:
            _require_provider(providers, component, "DmlExecutionProvider")

        LOGGER.info("engine_bundle_ready providers=%s", providers)
        return cls(classifier, ocr, wired, lineless, providers)

    @staticmethod
    def _require_models(model_dir: Path) -> None:
        required = [model_dir / path for path in TABLE_MODEL_PATHS.values()]
        required.extend(model_dir / "rapidocr" / name for name in RAPIDOCR_MODEL_NAMES)
        missing = [str(path) for path in required if not path.is_file()]
        if missing:
            raise FileNotFoundError(
                "Required model files are missing; run the prefetch command: "
                + ", ".join(missing)
            )

    def extract(self, image: np.ndarray) -> ExtractionResult:
        try:
            classification, _ = self.classifier(image)
            table_type = "wired" if classification == "wired" else "lineless"

            ocr_output = self.ocr(image)
            ocr_result = build_table_ocr_result(ocr_output)
            if not ocr_result:
                raise NO_TABLE.exception()

            engine = self.wired if table_type == "wired" else self.lineless
            output = engine(image, ocr_result)
            predicted_html = getattr(output, "pred_html", None)
            if not predicted_html or not predicted_html.strip():
                raise NO_TABLE.exception()

            return ExtractionResult(predicted_html, table_type)
        except ServiceError:
            raise
        except Exception as exception:
            raise INFERENCE_FAILED.exception() from exception


def _providers(component: Any) -> list[str]:
    current = component
    visited: set[int] = set()
    while current is not None and id(current) not in visited:
        visited.add(id(current))
        get_providers = getattr(current, "get_providers", None)
        if callable(get_providers):
            return list(get_providers())
        session = getattr(current, "session", None)
        if session is not None and session is not current:
            current = session
            continue
        engine = getattr(current, "engine", None)
        if engine is not None and engine is not current:
            current = engine
            continue
        break
    return []


def _require_provider(
    providers: dict[str, list[str]], component: str, provider: str
) -> None:
    active_providers = providers[component]
    if not active_providers or active_providers[0] != provider:
        raise RuntimeError(
            f"{component} did not initialize with required primary provider "
            f"{provider}: {active_providers}"
        )
