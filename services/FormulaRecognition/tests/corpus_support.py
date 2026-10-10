"""Conservative reference comparison for tests/benchmarks, never response edits."""
from __future__ import annotations

import re


def canonical_latex(value: str) -> str:
    # Math-mode whitespace and delimiter sizing do not change the formula.
    # Keep spaces inside explicit text commands: they can carry real meaning.
    text = re.compile(r"\\(?:text|textrm|textnormal)\{[^{}]*\}")
    chunks = []
    cursor = 0
    for match in text.finditer(value):
        chunks.append(_math_tokens(value[cursor:match.start()]))
        chunks.append(match.group())
        cursor = match.end()
    chunks.append(_math_tokens(value[cursor:]))
    return "".join(chunks)


def _math_tokens(value: str) -> str:
    value = re.sub(r"\\(?:left|right|displaystyle|textstyle)\b", "", value)
    value = re.sub(r"\\(?:,|;|!|quad\b|qquad\b)", "", value)
    value = re.sub(r"\\(?:dfrac|tfrac)\b", r"\\frac", value)
    value = re.sub(r"\\rightarrow\b", r"\\to", value)
    value = re.sub(r"\\ge\b", r"\\geq", value)
    value = re.sub(r"\\le\b", r"\\leq", value)
    value = re.sub(r"(?<!\\)\\ ", "", value)
    value = re.sub(r"\s+", "", value)
    # These standard operators have the same mathematical meaning as their
    # named TeX forms; do not normalize arbitrary operator names or grouping.
    return re.sub(r"\\operatorname\*?\{(exp|sin|cos|tan|log|ln|lim)\}",
                  lambda match: "\\" + match.group(1), value)


def matches_expected(actual: str, fixture: dict) -> bool:
    return canonical_latex(actual) in {
        canonical_latex(expected)
        for expected in [fixture["expected_latex"], *fixture.get("equivalents", [])]
    }
