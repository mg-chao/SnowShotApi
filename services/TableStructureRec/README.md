# TableStructureRec service

This Windows-only service accepts one cropped WebP table image at
`POST /v2/table/extract` with a raw `image/webp` body. It classifies the table, runs OCR once, selects the
wired or lineless structure engine, and returns `{"html":"..."}`.

The image dimensions may not exceed 2880 pixels wide by 2880 pixels high, and the
raw image payload may not exceed 800 KiB (819200 bytes).

## Runtime configuration

The defaults are suitable for local-only development. Production traffic uses
a private Tailscale address and mutual TLS configured through environment
variables or WinSW `<env>` entries.

| Variable | Default |
| --- | --- |
| `TABLE_REC_HOST` | `127.0.0.1` |
| `TABLE_REC_PORT` | `18080` |
| `TABLE_REC_WORKERS` | `3` |
| `TABLE_REC_MODEL_DIR` | `./models` |
| `TABLE_REC_LOG_LEVEL` | `info` |
| `TABLE_REC_ENVIRONMENT` | `development` |
| `TABLE_REC_WATCHDOG_SECONDS` | `55` |
| `TABLE_REC_TLS_CERTIFICATE` | unset |
| `TABLE_REC_TLS_PRIVATE_KEY` | unset |
| `TABLE_REC_TLS_CLIENT_CA` | unset |

WinSW sets the model directory to its deployment `models` folder. Set other
values as system environment variables before service startup, or add matching
`<env>` entries to `TableStructureRecService.xml`.

The port must be between 1 and 65535, workers between 1 and 64, and log level
one of `critical`, `error`, `warning`, `info`, `debug`, or `trace`. Empty hosts,
partial TLS triples, watchdog values outside 1 to 55 seconds, and TLS paths
that are not readable files fail before the HTTP server starts.

Set all three TLS paths together; partial TLS configuration fails startup. The
certificate must cover the worker's private DNS name or Tailscale IP and the
client CA must issue only gateway client certificates. Keep the Windows
firewall closed to non-Tailscale interfaces. Three worker processes are the
default, and every process loads a complete DirectML model bundle, so verify
GPU memory and throughput for the deployment before changing the worker count.

Set `TABLE_REC_ENVIRONMENT=staging` or `production` for deployed instances.
Every non-development startup requires `TABLE_REC_TLS_CERTIFICATE`,
`TABLE_REC_TLS_PRIVATE_KEY`, and `TABLE_REC_TLS_CLIENT_CA`. The server then
requires a client certificate signed by that CA. The API client separately
verifies the worker certificate chain and hostname, so its configured worker URL
must use a name present in the worker certificate.

## Concurrency and recovery

Each worker process exposes exactly one non-queuing inference slot. With the
default of three worker processes, the service can run up to three native
inferences at once. A request arriving while native inference is active in a
process receives the structured `worker_busy` response immediately; requests
are never accumulated in an internal queue. If the caller disconnects, the
request is canceled but the slot remains occupied until the native DirectML
executor actually finishes, preventing overlapping GPU work in that process.

Native inference is limited by `TABLE_REC_WATCHDOG_SECONDS`, capped at 55
seconds. A call that outlives the watchdog is treated as a potentially poisoned
DirectML runtime: the process logs a structured critical event and exits with
code 70. WinSW restarts the service according to
`deployment/TableStructureRecService.xml`. Monitor `worker_busy` responses,
watchdog events, process exit code 70, and WinSW restart counts.

## Install

Run from an elevated PowerShell prompt on Windows with Python 3.12 installed,
supplying the private bind address and all mutual-TLS files:

```powershell
.\scripts\Install.ps1 `
  -ListenHost "100.64.0.25" `
  -TlsCertificate "C:\ProgramData\SnowShot\pki\worker.pem" `
  -TlsPrivateKey "C:\ProgramData\SnowShot\pki\worker-key.pem" `
  -TlsClientCa "C:\ProgramData\SnowShot\pki\api-client-ca.pem"
```

The installer renders a deployment-specific WinSW configuration and validates
all TLS paths before registering the service. The service runs as
`NT AUTHORITY\LocalService` by default; the installer grants that identity
read/execute access to the runtime and TLS material and modify access only to
the log directory. A gMSA can be supplied with `-ServiceAccount`. LocalSystem is
rejected outside Development. Use
`-ServiceEnvironment development -ListenHost 127.0.0.1` only for a local
non-TLS service installation.

Installation stages the local `table_cls`, `wired_table_rec`, and
`lineless_table_rec` source packages, creates an isolated virtual environment,
installs pinned dependencies, verifies that base `onnxruntime` is absent,
downloads all models, loads every engine in a preflight, registers WinSW, and
starts the service.
Runtime startup uses explicit local model paths and does not download files.

`requirements-windows.lock` is the Python 3.12 Windows dependency lock and
contains hashes for every distribution. Bootstrap installs it with
`pip --require-hashes`. `model-manifest.json` pins the upstream model revision
to an immutable commit and provides a SHA-256 digest for every model artifact.
Downloads are written atomically, and both existing cache entries and newly
downloaded files must match the manifest before the DirectML engine preflight
runs. A hash mismatch aborts installation or startup rather than using the
artifact.

`requirements-test.lock` extends the same hash-locked runtime graph with the
test-only dependencies. CI installs that file with `pip --require-hashes` before
running the service-shell and recognition regression suites.

Lifecycle commands are `Start.ps1`, `Stop.ps1`, and `Uninstall.ps1`. Uninstall
removes only the Windows service registration; the model cache, virtual
environment, logs, and staged service files remain for recovery or audit.

## Verify

```powershell
python -m pytest -q
Invoke-RestMethod http://127.0.0.1:18080/health/live
Invoke-RestMethod http://127.0.0.1:18080/health/ready
```

DirectML is required throughout the extraction path: table classification,
RapidOCR, and both wired and lineless structure recognition. Startup preflight
checks every ONNX session and fails if any stage falls back to another provider.

The recognition packages were imported from RapidAI/TableStructureRec commit
`f79a85dbcbc0c14a5b5a14491a51d0855334d859`. The original project documentation
is retained as `README.upstream.md` and `README.upstream.en.md`.
