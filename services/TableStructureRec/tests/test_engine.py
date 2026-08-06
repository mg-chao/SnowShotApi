from __future__ import annotations

from types import SimpleNamespace

import numpy as np
import pytest

from lineless_table_rec.utils.utils_table_recover import (
    plot_html_table as plot_lineless_html,
)
from lineless_table_rec.table_structure_lore import TSRLore
from table_rec_service.engine import build_table_ocr_result
from wired_table_rec.utils.utils_table_recover import (
    plot_html_table as plot_wired_html,
)


@pytest.mark.parametrize("renderer", [plot_wired_html, plot_lineless_html])
def test_ocr_text_is_rendered_without_rewriting(renderer) -> None:
    original = '<script>"x" & \'y\'\nnext > previous < end'
    rendered = renderer([[0, 0, 0, 0]], {0: [original]})

    assert original in rendered
    assert "<br>" not in rendered
    assert rendered.startswith("<html><body><table>")


def test_ocr_boxes_text_and_scores_are_passed_once() -> None:
    output = SimpleNamespace(
        boxes=np.array([[[0, 0], [1, 0], [1, 1], [0, 1]]]),
        txts=["a&b"],
        scores=[0.9],
    )

    result = build_table_ocr_result(output)

    assert len(result) == 1
    assert result[0][1] == "a&b"
    assert result[0][2] == 0.9


def test_lore_process_uses_fixed_directml_batches() -> None:
    class ProcessSession:
        def __init__(self) -> None:
            self.use_directml = True
            self.input_shapes = []

        def __call__(self, inputs):
            logi_features, det_features = inputs
            self.input_shapes.append((logi_features.shape, det_features.shape))
            values = np.arange(54, dtype=np.float32).reshape(1, 54, 1)
            output = np.repeat(values, 4, axis=2)
            return output, output

    lore = TSRLore.__new__(TSRLore)
    lore.process_session = ProcessSession()
    logi_features = np.zeros((1, 55, 256), dtype=np.float32)
    det_features = np.zeros((1, 55, 8), dtype=np.int32)

    output = lore._run_process(logi_features, det_features)

    assert lore.process_session.input_shapes == [
        ((1, 54, 256), (1, 54, 8)),
        ((1, 54, 256), (1, 54, 8)),
    ]
    assert output.shape == (1, 55, 4)
    assert output[0, 53, 0] == 53
    assert output[0, 54, 0] == 0
