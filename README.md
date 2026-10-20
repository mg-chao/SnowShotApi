# SnowShot API

API and recognition releases: [blue/green deployment](docs/bluegreen-deployment.md).

LaTeX extraction: `POST /api/v1/latex/extract`, priced at ¥0.015 per successful
cropped-formula extraction using PP-FormulaNet_plus-S on Ubuntu/WSL CPU.
See the [worker guide](services/FormulaRecognition/README.md)
and [deployment runbook](docs/latex-deployment.md).

License: [AGPL-3.0](LICENSE).
