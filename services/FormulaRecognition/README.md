# Formula recognition worker

The Linux CPU worker recognizes one cropped printed mathematical formula with
**PP-FormulaNet_plus-S**. Ubuntu and WSL development are supported. Run it in an
isolated Python 3.12 environment; the table worker keeps its own runtime.
Install Ubuntu prerequisites `libgl1`, `libglib2.0-0`, and `libgomp1` before the
Python dependencies. Paddle CPU requires the OpenMP runtime from `libgomp1`.

```bash
python3.12 -m venv /path/to/latex-venv
/path/to/latex-venv/bin/python -m pip install --require-hashes -r requirements-cpu.lock
/path/to/latex-venv/bin/python -m latex_rec_service.prefetch --model-dir ./models
/path/to/latex-venv/bin/python -m latex_rec_service
```

Prefetch verifies the official archive, every asset size/SHA-256, and native
recognition before publication. Service startup requires those exact local
assets and performs no download. PaddleX 3.7.2 and PaddlePaddle 3.3.1 are pinned;
the completion adapter depends on their raw token interface. Upgrade them only
together with model-integrity, completion, accuracy, and performance checks.

## HTTP contract

`POST /v2/latex/extract` takes raw `image/jpeg`, `image/png` or `image/webp` bytes and returns
`{"latex":"..."}`. The public API remains `/api/v1/latex/extract`, with its
existing multipart envelope, successful-extraction charge, and accounting.
The private worker accepts static JPEG, PNG or WebP up to 819200 bytes and 2880 pixels in
either dimension. The declared media type must match the decoded image format.
EXIF orientation is applied and alpha is composited over white, producing RGB
input for the existing engine. Dark backgrounds are inverted
once before official PaddleX preprocessing. Uniform images return `no_formula`.

Errors retain their existing statuses: `413 payload_too_large`, `415 unsupported_image_format`,
`422 invalid_image/image_too_large/no_formula`, `500 inference_failed`, and
`503 worker_busy/worker_unavailable`. Header/body validation precedes admission;
when occupied, the native slot returns busy before detailed image decoding.
Generation without EOS is an inference failure rather than a successful partial
formula. The worker returns decoded syntax without heuristic mathematical fixes.

Roll out the updated formula Worker first, then API, then Mini Program. Existing
desktop WebP requests, public response envelopes, membership benefits and billing
remain compatible. The API adapter also understands the previous worker's
`not_webp` error during rollout. The `decode_webp` helper delegates to the shared
decoder for existing corpus and CPU benchmark scripts.

## Focused format checks

Run `python -m pytest -q -W error services/FormulaRecognition/tests/test_api.py
services/FormulaRecognition/tests/test_image_formats.py
services/FormulaRecognition/tests/test_engine.py
services/FormulaRecognition/tests/test_inference_gate.py` from the repository root
(join the command onto one line). These use a fake inference bundle and require
no model runtime.

For the real Worker → multipart API check, start the updated worker, set
`SNOWSHOT_LATEX_TEST_WORKER_URL` to its development HTTP origin, and set
`SNOWSHOT_LATEX_TEST_IMAGE_DIRECTORY` to a directory containing
`fractions-light.webp`, `fractions-light.jpeg` and `fractions-light.png`.
Use the committed `tests/corpus/fractions-light.webp` as the source; save RGB
copies with Pillow as JPEG (quality 95) and PNG. Run
`dotnet test tests/SnowShotApi.Tests/SnowShotApi.Tests.csproj --filter FullyQualifiedName~LatexRecognitionIntegrationTests`.
The three cases exercise the real model and API while isolating accounting with
the existing in-memory ledger; they assert the formula, exact source preservation
and one successful settlement. They skip unless both explicit variables are set.

## Configuration and lifecycle

| Variable | Default / behavior |
| --- | --- |
| `LATEX_REC_MODEL_DIR` | `models` under this service |
| `LATEX_REC_HOST`, `LATEX_REC_PORT` | `127.0.0.1`, `18081` |
| `LATEX_REC_WORKERS` | `1`; other values reject startup to preserve one process and one admission slot |
| `LATEX_REC_CPU_THREADS` | `1`; positive and no greater than effective CPU affinity |
| `LATEX_REC_ENVIRONMENT` | `development`; staging/production require mTLS |
| `LATEX_REC_TLS_CERTIFICATE`, `LATEX_REC_TLS_PRIVATE_KEY`, `LATEX_REC_TLS_CLIENT_CA` | Configure all three together |
| `LATEX_REC_STARTUP_WATCHDOG_SECONDS` | `120`, range 1–300 |
| `LATEX_REC_WATCHDOG_SECONDS` | `55`, range 1–55 |
| `LATEX_REC_LOG_LEVEL` | `info` |

Remove `LATEX_REC_EXECUTION_PROVIDER`; its presence rejects startup. CPU Paddle
inference is fixed, with MKLDNN/HPI disabled. OpenCV, OpenMP, OpenBLAS, MKL,
NumExpr, and tokenizer parallelism outside inference are constrained by the CLI
and production unit.

One dedicated owner thread initializes, warms, decodes, recognizes, and disposes
each predictor. There is one non-queuing native slot. HTTP cancellation retains
that slot and its independent watchdog until native work finishes. Startup,
inference, and disposal hangs exit with code 70 for supervisor recovery. Health
requests remain responsive during admitted inference. Readiness is advertised
only after verified loading and warmup. Logs contain IDs, runtime details, phase,
duration, and error categories, without image or formula contents.

The production CLI protects native startup from Paddle's SIGTERM handler
replacement. A stop during initialization waits for native completion (or the
startup watchdog); Uvicorn then drains and disposes the predictor. The server's
handler and original main-thread signal mask are restored before readiness.
Normal graceful shutdown uses 55 seconds, with the supervisor's existing
60-second stop limit enforcing the outer bound.

## Verification and accuracy

```bash
/path/to/latex-venv/bin/python -m pip install --require-hashes -r requirements-test-cpu.lock
LATEX_REC_REQUIRE_MODELS=1 /path/to/latex-venv/bin/python -m pytest -q tests
```

`tests/corpus/manifest.json` contains 52 reference images: 48 variants of 12
independently authored TeX references and four historical RapidAI fixtures.
The generator uses TeX Live/dvipng and Pillow; committed assets allow offline CI.
Reference comparison permits reviewed equivalent LaTeX syntax and never edits
production responses. The four historical RapidAI images retain their MIT
license and provenance in `tests/test_files`.

The pinned S model has genuine limitations on nine challenge fixtures: an extra
brace in cases, a duplicated symbol in aligned equations, and an incorrect
differential/fraction in one scaled integral. These remain strict expected-failure
tests against unchanged ground truth. They are included in accuracy reports and
explicitly excluded from performance selection, which requires correct outputs.
An unexpected pass forces review of the model/corpus rather than silently changing
the baseline. This model does not guarantee correctness for every printed formula.
The nine known semantic misses return HTTP 200 and retain successful-extraction
billing; token completion does not verify mathematical accuracy.

Use `scripts/benchmarks/formula_accuracy.py` from the repository root to save the
complete accuracy results, including challenge failures. See
[`docs/latex-deployment.md`](../../docs/latex-deployment.md) for production staging,
CPU tuning, cutover, monitoring, and retained-release rollback. Local WSL measurements
are development evidence; the live server is not changed by this replacement.
