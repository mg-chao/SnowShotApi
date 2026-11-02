from tests.corpus_support import canonical_latex, matches_expected


def test_math_spacing_and_sizing_are_equivalent():
    assert canonical_latex(r"\left( x + y \right)\,\dfrac{1}{2}") == r"(x+y)\frac{1}{2}"


def test_text_spacing_and_math_structure_are_preserved():
    assert canonical_latex(r"\text{a b}") != canonical_latex(r"\text{ab}")
    reference = {"expected_latex": r"x^{2}+y^{2}=1"}
    assert matches_expected(r"x^{2} + y^{2} = 1", reference)
    assert not matches_expected(r"x^{2}-y^{2}=1", reference)
    assert canonical_latex(r"{x+y}^{2}") != canonical_latex(r"x+y^{2}")


def test_standard_operator_spellings_are_equivalent():
    assert canonical_latex(r"\operatorname*{l i m}_{x\to0}\operatorname{s i n}x") == r"\lim_{x\to0}\sinx"


def test_row_separators_survive_spacing_normalization():
    assert canonical_latex(r"1&2\\ {3}&{4}\\ \end{matrix}") == r"1&2\\{3}&{4}\\\end{matrix}"
