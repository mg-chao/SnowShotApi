"""Regenerate independent reference images with TeX Live and dvipng.

Run on Linux with Python/Pillow. Neither the recognition model nor its outputs
participate in generation. Generated WebP assets are committed for offline CI.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parent
CORPUS = ROOT / "corpus"
REFERENCES = [
    ("fractions", r"\frac{a+b}{c-d}"),
    ("roots", r"x=\sqrt{a^{2}+b^{2}}"),
    ("greek", r"\alpha+\beta=\gamma"),
    ("powers", r"e^{i\pi}+1=0"),
    ("integral", r"\int_{0}^{1}x^{2}\,dx=\frac{1}{3}"),
    ("sum", r"\sum_{n=1}^{\infty}\frac{1}{n^{2}}=\frac{\pi^{2}}{6}"),
    ("limit", r"\lim_{x\to0}\frac{\sin x}{x}=1"),
    ("matrix", r"\begin{pmatrix}1&2\\3&4\end{pmatrix}"),
    ("cases", r"f(x)=\begin{cases}x^{2}&x\geq0\\-x&x<0\end{cases}"),
    ("multiline", r"\begin{aligned}a+b&=c\\a-b&=d\end{aligned}"),
    ("derivatives", r"\frac{\partial^{2}u}{\partial x^{2}}+\frac{\partial^{2}u}{\partial y^{2}}=0"),
    ("long", r"\left(\sum_{k=0}^{n}\binom{n}{k}a^{n-k}b^{k}\right)^{2}=(a+b)^{2n}"),
]
EQUIVALENTS = {
    "roots": [r"x={\sqrt{a^{2}+b^{2}}}"],
    "limit": [r"\operatorname*{lim}_{x\to0}\frac{\operatorname{sin}x}{x}=1"],
    "matrix": [r"\left(\begin{matrix}{1}&{2}\\{3}&{4}\\\end{matrix}\right)"],
    "long": [REFERENCES[-1][1] + "."],
    "derivatives": [r"{\frac{\partial^{2}u}{\partial x^{2}}}+{\frac{\partial^{2}u}{\partial y^{2}}}=0"],
}
EQUIVALENTS["limit"].append(r"\operatorname*{lim}_{x\to0}{\frac{\sin x}{x}}=1")
LIMITATIONS = {
    "cases": "Pinned S model emits an extra brace and invented punctuation for this cases example.",
    "multiline": "Pinned S model duplicates c in the first aligned equation.",
}


def main():
    CORPUS.mkdir(exist_ok=True)
    fixtures = []
    for category, formula in REFERENCES:
        with tempfile.TemporaryDirectory(prefix="formula-reference-") as temporary:
            directory = Path(temporary)
            source = (r"\documentclass[12pt]{article}" "\n"
                      r"\usepackage{amsmath,amssymb}" "\n"
                      r"\usepackage[active,tightpage]{preview}" "\n"
                      r"\PreviewEnvironment{equation*}" "\n"
                      r"\begin{document}\begin{equation*}" "\n" + formula + "\n"
                      r"\end{equation*}\end{document}" "\n")
            (directory / "reference.tex").write_text(source)
            subprocess.run(["latex", "-interaction=nonstopmode", "-halt-on-error", "reference.tex"],
                           cwd=directory, check=True, stdout=subprocess.DEVNULL)
            subprocess.run(["dvipng", "-D", "180", "-T", "tight", "-bg", "White", "-o", "reference.png", "reference.dvi"],
                           cwd=directory, check=True, stdout=subprocess.DEVNULL)
            with Image.open(directory / "reference.png") as rendered:
                normal = ImageOps.expand(rendered.convert("RGB"), border=16, fill="white")
            transparent = normal.convert("RGBA")
            # The independent fixture has black ink and a transparent white background.
            pixels = transparent.load()
            for y in range(transparent.height):
                for x in range(transparent.width):
                    red, green, blue, _ = pixels[x, y]
                    pixels[x, y] = (0, 0, 0, 255 - min(red, green, blue))
            variants = {
                "light": normal,
                "dark": ImageOps.invert(normal),
                "scaled": normal.resize((normal.width * 2, normal.height * 2), Image.Resampling.LANCZOS),
                "transparent": transparent,
            }
            for variant, image in variants.items():
                identifier = f"{category}-{variant}"
                path = CORPUS / f"{identifier}.webp"
                image.save(path, format="WEBP", lossless=True)
                fixtures.append({"id": identifier, "image": path.name, "category": category,
                                 "expected_latex": formula, "equivalents": EQUIVALENTS.get(category, []),
                                 "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
                limitation = LIMITATIONS.get(category)
                if identifier == "integral-scaled":
                    limitation = "Pinned S model misrecognizes the differential and fraction at this scale."
                if limitation:
                    fixtures[-1].update(known_limitation=limitation, benchmark=False)
    historical = {
        1: r"\operatorname{exp}\left[\int d^{4}x\,g\phi\bar{\psi}\psi\right]=\sum_{n=0}^{\infty}\frac{g^{n}}{n!}\left(\int d^{4}x\,\phi\bar{\psi}\psi\right)^{n}.",
        2: r"x^{2}+y^{2}=1",
        5: r"x=\frac{-b\pm\sqrt{b^{2}-4ac}}{2a}",
        6: r"\frac{x^{2}}{a^{2}}-\frac{y^{2}}{b^{2}}=1",
    }
    for number, formula in historical.items():
        path = CORPUS / f"upstream-{number}.webp"
        with Image.open(ROOT / f"test_files/{number}.png") as image:
            image.convert("RGB").save(path, format="WEBP", lossless=True)
        fixtures.append({"id": f"upstream-{number}", "image": path.name, "category": "historical",
                         "expected_latex": formula, "equivalents": [],
                         "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
        if number == 5:
            fixtures[-1]["equivalents"] = [r"x={\frac{-b\pm{\sqrt{b^{2}-4ac}}}{2a}}"]
        elif number == 6:
            fixtures[-1]["equivalents"] = [r"{\frac{x^{2}}{a^{2}}}-{\frac{y^{2}}{b^{2}}}=1"]
    (CORPUS / "manifest.json").write_text(json.dumps(fixtures, indent=2) + "\n")
    print(f"Generated {len(fixtures)} independent fixtures.")


if __name__ == "__main__":
    main()
