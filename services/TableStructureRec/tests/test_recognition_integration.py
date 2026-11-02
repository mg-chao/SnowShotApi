from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest
from PIL import Image, ImageDraw, ImageFont

pytest.importorskip("table_cls")
pytest.importorskip("wired_table_rec")
pytest.importorskip("lineless_table_rec")

from table_rec_service.engine import EngineBundle, build_table_ocr_result
from table_rec_service.config import ServiceSettings

MODEL_DIR = Path(__file__).resolve().parents[1] / "models"
REQUIRED_MODEL = MODEL_DIR / "wired" / "unet.onnx"
pytestmark = pytest.mark.skipif(
    not REQUIRED_MODEL.is_file(),
    reason="Run table_rec_service.prefetch before native recognition tests.",
)


@pytest.fixture(scope="module")
def bundle() -> EngineBundle:
    settings = ServiceSettings.from_environment()
    return EngineBundle.load(MODEL_DIR, settings.execution_provider, settings.cpu_threads)


def table_image(*, wired: bool) -> np.ndarray:
    image = Image.new("RGB", (1000, 500), "white")
    draw = ImageDraw.Draw(image)
    font = ImageFont.load_default(size=28)
    rows = (
        ("Product", "Quantity", "Price"),
        ("Notebook", "12", "5.00"),
        ("Marker", "6", "2.50"),
        ("Folder", "9", "3.25"),
    )
    if wired:
        for x in (30, 340, 650, 970):
            draw.line((x, 20, x, 480), fill="black", width=4)
        for y in (20, 135, 250, 365, 480):
            draw.line((30, y, 970, y), fill="black", width=4)

    for row_index, values in enumerate(rows):
        for column_index, value in enumerate(values):
            draw.text(
                (70 + column_index * 315, 60 + row_index * 115),
                value,
                fill="black",
                font=font,
            )
    return np.asarray(image)[:, :, ::-1].copy()


def test_provider_assignment(bundle: EngineBundle) -> None:
    for component in (
        "classifier",
        "ocr_det",
        "ocr_cls",
        "ocr_rec",
        "wired",
        "lineless_detect",
        "lineless_process",
    ):
        assert bundle.provider_summary[component][0] == ServiceSettings.from_environment().execution_provider


def test_cpu_threads_reach_every_session(bundle: EngineBundle) -> None:
    settings = ServiceSettings.from_environment()
    if settings.execution_provider != "CPUExecutionProvider":
        pytest.skip("CPU thread controls do not change DirectML sessions")
    components = [bundle.classifier.table_engine.table_cls, bundle.ocr.text_det,
                  bundle.ocr.text_cls, bundle.ocr.text_rec, bundle.wired.table_structure.session,
                  bundle.lineless.table_structure.det_session, bundle.lineless.table_structure.process_session]
    for component in components:
        while not hasattr(component, "get_session_options"):
            component = getattr(component, "session", None) or getattr(component, "engine")
        assert component.get_session_options().intra_op_num_threads == settings.cpu_threads


def test_wired_table_returns_non_empty_html(bundle: EngineBundle) -> None:
    result = bundle.extract(table_image(wired=True))

    assert result.table_type == "wired"
    assert result.html.startswith("<html><body><table>")
    assert "Notebook" in result.html


def test_lineless_table_returns_non_empty_html(bundle: EngineBundle) -> None:
    image = table_image(wired=False)
    ocr_result = build_table_ocr_result(bundle.ocr(image))
    result = bundle.lineless(image, ocr_result)

    assert result.pred_html.startswith("<html><body><table>")
    assert "Notebook" in result.pred_html
