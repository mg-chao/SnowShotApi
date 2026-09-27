# SnowShot LaTeX extraction worker

Converts one cropped formula image to LaTeX using the vendored RapidLaTeXOCR
source. SnowShot charges **0.015 yuan per successful extraction** (15,000,000
nano-yuan), independently configured from table extraction's 0.03 yuan rate.
Failed extraction incurs zero public charge.

## API

Public: `POST /api/v1/latex/extract`, multipart with exactly one file named
`image`. The existing SnowShot success envelope contains `data.latex`.

```sh
curl --fail-with-body https://snowshot.top/api/v1/latex/extract \
  -H 'X-Request-ID: formula-example-1' \
  -F 'image=@formula.webp;type=image/webp'
```

Private: `POST /v2/latex/extract`, raw `image/webp`, returning
`{"latex":"x^{2}+y^{2}=1"}`. Only static WebP is accepted, up to **819200 bytes
and 2880 pixels per dimension**. Transparency is composited onto white. Output
is formula text without added math delimiters or rendering. Upstream internally
resizes to 672 × 192 model bounds and decodes at most 512 tokens. This service
does not detect formulas in whole pages or guarantee rejection of arbitrary
non-formula content.

The public endpoint uses existing identity, request-ID, admission, quota,
reservation, and durable settlement handling. Defaults: six requests/minute
per principal, one concurrent request per principal and globally, eight queued
requests, 30-second queue wait, and 60-second execution deadline. There are no
automatic inference retries. Duplicate request IDs follow the existing 409
contract; use a fresh ID for a new attempt after terminal failure.

| Public status | Code | Meaning |
| --- | --- | --- |
| 400 | `invalid_request` | Invalid multipart, format, image, or dimensions |
| 413 | `payload_too_large` | Upload or multipart envelope exceeds its limit |
| 422 | `no_formula` | Blank image or empty recognition result |
| 502 | `inference_failed` | Inference failure or empty worker success |
| 503 | `worker_busy` | Worker occupied; retry guidance included |
| 503 | `latex_worker_unavailable` | Worker unreachable or response invalid |
| 504 | `deadline_exceeded` | Deadline expired |

Existing admission, budget, and ownership errors also apply. Private failures
use `{"error":{"code":"...","message":"..."}}`; `openapi.private.json`
records their status codes and is checked against generated OpenAPI.

## Local setup

Requires Windows, Python 3.12, and a DirectML-capable GPU/driver. From the repo:

```powershell
.\services\RapidLaTeXOCR\scripts\Bootstrap.ps1 -InstallRoot "$PWD\services\RapidLaTeXOCR"
Push-Location services\RapidLaTeXOCR
.\venv\Scripts\python.exe -m latex_rec_service.prefetch --model-dir models
.\venv\Scripts\python.exe -m latex_rec_service
Pop-Location
```

API setting: `Providers__Latex__BaseUrl=http://127.0.0.1:18081/`.
`compose.local-latex.yaml` connects a local containerized API to the host worker.
`/health/live` checks HTTP availability; `/health/ready` requires verified models
and a successful preflight exercising all inference stages. The API includes
`latex_worker` in readiness, so provision it before activating this API release.

## Runtime settings

| Variable | Default |
| --- | --- |
| `LATEX_REC_HOST` | `127.0.0.1` |
| `LATEX_REC_PORT` | `18081` |
| `LATEX_REC_WORKERS` | `1` |
| `LATEX_REC_MODEL_DIR` | service-local `models` |
| `LATEX_REC_LOG_LEVEL` | `info` |
| `LATEX_REC_ENVIRONMENT` | `development` |
| `LATEX_REC_WATCHDOG_SECONDS` | `55`, maximum 55 |
| `LATEX_REC_TLS_CERTIFICATE` | unset |
| `LATEX_REC_TLS_PRIVATE_KEY` | unset |
| `LATEX_REC_TLS_CLIENT_CA` | unset |

All TLS paths must be supplied together; staging and production require them.
The worker verifies client certificates. The API separately verifies the server
chain and hostname using `Providers:Latex`: `BaseUrl`, `ClientCertificatePath`,
`ClientCertificatePassword`, `ServerCaCertificatePath`, `MaximumUploadBytes`
(819200), and `MaximumResponseBytes` (2097152). Non-Development API startup
requires HTTPS and certificate files.

Each process owns one non-queuing native slot. Cancellation retains the slot
until native work ends. Inference exceeding the watchdog exits with code 70;
WinSW restarts the process. Measure GPU memory and contention with the table
worker before increasing worker count or API concurrency.

## Installation and supply chain

Follow [the deployment runbook](../../docs/latex-deployment.md). Install, start,
stop, uninstall, and WinSW configuration are supplied. Installation stages source,
creates an isolated runtime, installs hash-locked dependencies, verifies models
and DirectML preflight, grants LocalService access, and starts the service. Only
logs require write access. Uninstallation preserves the runtime, models, and logs.

Runtime never downloads models. `model-manifest.json` pins the four upstream
v0.0.0 release artifacts by SHA-256. Downloads are verified before atomic
replacement; startup verifies cached files. Dependency locks include hashes.
All three ONNX sessions explicitly initialize DirectML, disable provider failover,
use sequential execution, and disable memory patterns. ONNX Runtime may assign
unsupported individual operators to CPU; CPU-only sessions are rejected.

## Tests

From this service directory:

```powershell
.\venv\Scripts\python.exe -m pip install --require-hashes -r requirements-test.lock
.\venv\Scripts\python.exe -m pytest -q -W error tests
```

Tests cover uploads, errors, cancellation, watchdog recovery, model integrity,
WinSW configuration, and mTLS. Native tests convert upstream fixtures to lossless
WebP, extract them through HTTP, and check the formulas. They require prefetched
models and DirectML; without models they are explicitly skipped. CI uses a
separate environment from TableStructureRec because NumPy requirements differ.

Source revision and patches: `UPSTREAM.md`. Original docs: `README.upstream.md`.
Original tests: `tests/upstream_main.py`. Upstream licensing: `LICENSE`.
