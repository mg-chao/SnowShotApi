# SnowShot API

API and recognition releases: [blue/green deployment](docs/bluegreen-deployment.md).

Local debugging uses `http://localhost:5101`, both for the HTTP launch profile and
the development Docker Compose host port.

Table extraction: `POST /api/v1/table/extract` accepts one multipart `image` file
in JPEG, PNG or WebP format, up to 800 KiB and 2880 × 2880 pixels, and returns
`{code:0,data:{html:...}}`. See the [table worker guide](services/TableStructureRec/README.md)
for media types, validation and Worker → API → Mini Program rollout order.

LaTeX extraction: `POST /api/v1/latex/extract` accepts one multipart `image` file
in JPEG, PNG or WebP format, up to 800 KiB and 2880 × 2880 pixels, and returns
`{code:0,data:{latex:...}}`. Existing optional account authentication and membership
benefits apply. Standard usage is priced at ¥0.015 per successful
cropped-formula extraction using PP-FormulaNet_plus-S on Ubuntu/WSL CPU.
See the [worker guide](services/FormulaRecognition/README.md)
and [deployment runbook](docs/latex-deployment.md).

License: [AGPL-3.0](LICENSE).
