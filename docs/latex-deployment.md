# LaTeX extraction deployment

For active blue/green releases, use the [release guide](bluegreen-deployment.md)
and its `status` command to identify the selected worker units and private ports.
The fixed-port restart and rollback commands below describe the retained legacy
layout; do not apply them to an active blue/green release.

The formula worker uses **PP-FormulaNet_plus-S on Ubuntu/WSL CPU**. Its source is
`services/FormulaRecognition`; its module remains `latex_rec_service`. The
private route, port 18081, certificate identities, public API and billing stay
compatible with the previous deployment.

## Stage a new worker release

Use a separate release directory and a provisioned Python 3.12 runtime with
`venv` support; the Ubuntu system Python may be a different version. Install the
native libraries and hash-locked CPU dependencies, then fetch and verify the
pinned model:

```bash
sudo apt-get update
sudo apt-get install -y libgl1 libglib2.0-0 libgomp1 openssl curl
cd /path/to/release/services/FormulaRecognition
python3.12 -m venv /path/to/release/latex-venv
/path/to/release/latex-venv/bin/python -m pip install --require-hashes -r requirements-cpu.lock
/path/to/release/latex-venv/bin/python -m latex_rec_service.prefetch --model-dir models
```

Runtime startup verifies every model file and performs a complete recognition
preflight. Runtime does not download models. Leave source, models and the
environment read-only to the worker; its logs and temporary directory may be
writable. Use the separate LaTeX PKI bundle under `/etc/snowshot/pki-latex`.
`libgomp1` supplies Paddle's CPU OpenMP runtime and is required on minimal Ubuntu
installations even when the existing ONNX worker already runs successfully.

Set `LATEX_REC_CPU_THREADS=1`, `LATEX_REC_WORKERS=1`,
`LATEX_REC_HOST=127.0.0.1`, `LATEX_REC_PORT=18081`,
`LATEX_REC_ENVIRONMENT=production`, and `LATEX_REC_MODEL_DIR` to the new models.
Supply all three TLS paths: `LATEX_REC_TLS_CERTIFICATE`,
`LATEX_REC_TLS_PRIVATE_KEY` and `LATEX_REC_TLS_CLIENT_CA`.
Remove `LATEX_REC_EXECUTION_PROVIDER`; it is obsolete and startup rejects it.
OpenCV/OpenMP/OpenBLAS/MKL/NumExpr are constrained to one external thread.
Paddle static CPU uses the configured inference thread count with MKLDNN/HPI
disabled.

The selected eight-core production configuration gives both workers access to
all eight physical cores, with eight table threads and one formula thread,
one process per service. On the current sixteen-logical-CPU host, both affinity
masks are `0–15`. Confirm topology and effective affinity with
`lscpu -e=CPU,CORE,SOCKET` before applying masks on another host.
This allocation was selected from WSL development measurements and explicitly
adopted for production; it is not a target-host capacity guarantee. Retain the
previous disjoint 7/1 allocation in the rollback configuration.

## Verify before switching traffic

Install `requirements-test-cpu.lock` in the staged environment. Run
`LATEX_REC_REQUIRE_MODELS=1 python -m pytest -q -W error -ra tests` from the
service directory; this makes unavailable real-model prerequisites a failure.

Start the staged worker on another loopback port with the production mTLS
settings. The certificate and probe utilities in
`services/FormulaRecognition/scripts/deployment` can provision new identities.
Reuse their certificate files on Ubuntu; the retained PKI generator's
`latex-worker.env` contains Windows TLS paths and must be replaced with the
Linux worker environment. Set its certificate, private key and client CA paths
to `/etc/snowshot/pki-latex/worker-server.pem`,
`/etc/snowshot/pki-latex/worker-server-key.pem` and
`/etc/snowshot/pki-latex/api-client-ca.pem`, respectively. Existing certificate
identities and tunnel hostnames remain valid.
Probe authenticated readiness and known formulas. An unauthenticated client
must be rejected. Run the balanced CPU sweep in
[the CPU deployment guide](ubuntu-cpu-recognition.md) on production-equivalent
hardware when evaluating further tuning. Monitor the adopted shared-core
configuration under representative production demand.

For example, after configuring the staged release environment and TLS paths,
start it from its service directory on port 28081:

```bash
LATEX_REC_PORT=28081 /path/to/release/latex-venv/bin/python -m latex_rec_service
```

In another terminal, use an authorized probe certificate issued by the configured
LaTeX client CA. Keep its private key outside the release and reports. The server
certificate identity remains `snowshot-latex-rec`; `--resolve` connects directly
to the staged loopback port while retaining hostname verification:

```bash
probe_pki=/path/to/authorized-probe
curl --fail --silent --show-error --connect-timeout 5 --max-time 60 \
  --cacert "$probe_pki/worker-server-ca.pem" \
  --cert "$probe_pki/client.pem" --key "$probe_pki/client-key.pem" \
  --resolve snowshot-latex-rec:28081:127.0.0.1 \
  https://snowshot-latex-rec:28081/health/ready

curl --fail --silent --show-error --connect-timeout 5 --max-time 60 \
  --cacert "$probe_pki/worker-server-ca.pem" \
  --cert "$probe_pki/client.pem" --key "$probe_pki/client-key.pem" \
  --resolve snowshot-latex-rec:28081:127.0.0.1 \
  -H 'Content-Type: image/webp' -H 'X-Request-ID: staging-formula-check' \
  --data-binary @tests/corpus/fractions-light.webp \
  https://snowshot-latex-rec:28081/v2/latex/extract
```

Require `{"status":"ready"}` and the reviewed fraction `\frac{a+b}{c-d}`.
Repeat the readiness command without `--cert` and `--key`; it must fail TLS
authentication. Stop the staged process after verification. Preserve the
authenticated checks for the production port immediately after cutover.

The pinned model has nine known semantic misses in the 52-image challenge corpus.
They return HTTP 200 and retain successful-extraction billing; readiness and
token completion do not verify mathematical accuracy. Review representative
printed formulas as well as the transport checks before cutover.

Keep API policy revision 13 and global/per-principal concurrency 1/1. Worker
threads and CPU affinity can change independently of API policy. A model
replacement requires no accounting migration or price change: successful
formula extraction still costs 15,000,000 nano-yuan; failures cost zero.

## Switch and monitor

Back up the worker unit, environment file and runner. Stage the new source,
environment and verified models, then point `run-worker latex` to the new
release. Restart only `snowshot-cpu-worker@latex`, confirm authenticated
readiness, and retain the existing restricted reverse SSH tunnel. The API URL
remains `https://snowshot-latex-rec:18081/`.
Applying the selected shared-core allocation also requires updating the table
thread count to eight, setting both affinity masks to all eight cores, and
restarting the table worker. Its source, environment dependencies and models
remain unchanged. Verify both existing tunnels after worker restarts.

`scripts/ubuntu/Install-UbuntuRecognition.sh` installs both workers from an
already staged layout; it does not fetch Python, dependencies or models.
Its formula source path is `services/FormulaRecognition`.
Do not run the combined installer merely to restart an existing formula worker.

Monitor worker readiness, latency, busy outcomes, watchdog exits (70), restarts,
RSS, swap, CPU temperature and table contention. Logs must not contain uploaded
images or generated formulas. Native cancellation holds the occupied slot until
inference ends; inference beyond 55 seconds terminates the process for systemd
recovery.

SIGTERM during native startup is deferred until initialization finishes or the
startup watchdog exits 70. The worker restores Uvicorn's signal handler and the
main thread's original mask before readiness; predictor disposal runs on its
owner thread. Keep the 55-second graceful shutdown setting and the existing
60-second supervisor stop limit. That outer limit also applies when stopping a
worker whose startup is still blocked.

## Rollback

Keep the prior deployed worker source, locked environment, verified models,
configuration and runner together as an **external release artifact**. Rollback
restores that artifact and its matching environment configuration; it does not
restore old code into the new repository. In particular, the previous ONNX
runtime requires its old provider setting, while the new Paddle worker rejects
that setting.

Retained WSL releases may still use their old source paths. The
`scripts/ubuntu/Restore-WslRecognition.ps1` helper switches retained external
workers/tunnels; it does not install the new model into them. Verify both worker
components and public readiness after any host switch. Windows/DirectML formula
installation is no longer supported by this release.

Keep policy revision 13 during a worker rollback. Accounting records and the
existing additive usage-kind schema remain intact. A separate policy rollback
requires the normal higher-revision recovery procedure.
