from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest
from PIL import Image, ImageDraw, ImageFont

pytest.importorskip("table_cls")
pytest.importorskip("wired_table_rec")
pytest.importorskip("lineless_table_rec")

from table_rec_service.engine import EngineBundle, build_table_ocr_result

MODEL_DIR = Path(__file__).resolve().parents[1] / "models"
REQUIRED_MODEL = MODEL_DIR / "wired" / "unet.onnx"
pytestmark = pytest.mark.skipif(
    not REQUIRED_MODEL.is_file(),
    reason="Run table_rec_service.prefetch before native recognition tests.",
)


@pytest.fixture(scope="module")
def bundle() -> EngineBundle:
    return EngineBundle.load(MODEL_DIR)


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
        assert bundle.provider_summary[component][0] == "DmlExecutionProvider"


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
