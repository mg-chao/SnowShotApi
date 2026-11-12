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

Each IP address gets 10 free table extractions, 10 free LaTeX extractions, and
5 free Fast Model (`ai-model`) requests per calendar month. The allowances reset
at midnight Asia/Shanghai on the first day of each month. Anonymous and signed-in
requests from the same IP share each allowance. Only delivered results count.
Linked accounts can continue with wallet credits, and qualifying members have free
table and LaTeX extraction independently of these allowances. When an anonymous
request runs out of slots, the API returns `429 allowance_exhausted` with
`Retry-After` indicating when to retry.

License: [AGPL-3.0](LICENSE).
