# Ubuntu CPU recognition deployment and benchmark

Use [blue/green deployment](bluegreen-deployment.md) for current release changes.
Its manifests identify active units and ports; the fixed-port installation and
recovery examples here describe the legacy layout and retained benchmark evidence.

## PP-FormulaNet_plus-S release

The current repository replaces the formula worker with
`services/FormulaRecognition` on Ubuntu/WSL CPU. See
[the formula rollout guide](latex-deployment.md). The HTTP module remains
`latex_rec_service`, the private endpoint remains port 18081, and API policy
revision 13 remains unchanged. This repository change does not itself deploy a
new worker to the server.

Use the new service's `requirements-cpu.lock` or
`requirements-test-cpu.lock` in an isolated Python 3.12 environment. Prefetch
verifies the pinned PP-FormulaNet_plus-S archive and model files; startup works
offline and verifies the local files again. Remove
`LATEX_REC_EXECUTION_PROVIDER` from the new worker environment. Its fixed backend
is Paddle static CPU with MKLDNN/HPI disabled. Only the table worker retains
`TABLE_REC_EXECUTION_PROVIDER=CPUExecutionProvider`.

The adopted default uses one process per service, table eight CPU threads/formula
one CPU thread, with both services sharing all eight physical cores. It was
selected in WSL and explicitly adopted for production; target-host monitoring
is still required. Table implementation,
dependencies, model artifacts and ONNX options are unchanged. Threads and
affinity are worker settings; changing them does not require an API policy
revision. Multiple formula processes would not improve admitted throughput
while API global concurrency remains one.

### Balanced tuning

Run the bounded sweep on an otherwise idle eight-physical-core Ubuntu host.
The host's `performance` governor should match the retained deployment. The
runner discovers SMT siblings with `lscpu`; its CPU choices are saved in every
report. Existing models and separate environments can be supplied explicitly:

```bash
latex-venv/bin/python scripts/benchmarks/sweep_formula_cpu.py \
  --root "$PWD" \
  --table-python "$PWD/table-venv/bin/python" \
  --latex-python "$PWD/latex-venv/bin/python" \
  --table-model-dir "$PWD/services/TableStructureRec/models" \
  --latex-model-dir "$PWD/services/FormulaRecognition/models" \
  --pki-dir /path/to/temporary-probe-pki \
  --output-dir /path/to/fresh/formula-sweep
```

The temporary PKI directory contains `table-communication` and
`latex-communication`, each with `worker-server.pem`, `worker-server-key.pem`,
`api-client-ca.pem`, `worker-server-ca.pem`, `client.pem`, and `client-key.pem`.
Do not use or copy production private keys into benchmark reports. Temporary
workers bind random loopback ports and verify both server and client identities;
the runner never changes systemd units, tunnels, API policy or live routing.

The 12 native configurations are disjoint 7/1, 6/2, 5/3 and 4/4 physical-core
splits, using table threads equal to table cores and formula thread counts in
the unique set `{1, min(2, formula cores), formula cores}`; three more cases
share all cores with table eight threads/formula one, two or four threads.
Every case uses one process per service and measures 90 seconds after both
workers finish warming the reviewed performance corpus. Its manifest explicitly
excludes known model limitations from performance timing; omitted IDs and reasons
are reported, while the accuracy suite still exercises every fixture.
Native and HTTP measurements use the same deterministic round-robin formula
order. The `-light`, `-dark`, `-scaled` and `-transparent` ID suffixes identify
variants of a base formula; each historical ID is a separate base. Base order
and variant order follow the manifest, so the first 14 requests cover all 14
retained bases before any base repeats. All 43 fixture bytes and expectations
are preserved. This spreads formula complexity across short timed prefixes;
time-limited runs may still complete different numbers of variants. A readiness
barrier starts both measurements together.

The top two native candidates plus the new-model 7/1 reference undergo three
180-second balanced mTLS runs, followed by a 20-minute selected-configuration
soak. Formula backlog is bounded to eight; reports distinguish worker HTTP
latency from offer-to-completion delay. P95 consistently uses nearest rank.
Selection maximizes median balanced completed pairs/second and requires meeting
the 7/1 reference in a majority of paired rounds. Median rates within 3% of the
best prefer fewer formula cores, then fewer threads. Correctness errors, swap and
worker exits reject a run. RSS windows remain in the soak report for checking
whether warm allocations stabilize.

Formula correctness is checked against the reviewed expected syntax. Table
validation checks only a nonempty string containing `<table>`; it does not score
cell transcription or structural correctness. Completed-pair rates therefore
count completed tables with matched formulas under that limited table check.

`--stage screen` runs only native measurements without PKI. Continue a completed
screen with `--reuse-screen` and the same directory plus PKI. Reuse requires a
matching fingerprint of model files/manifests, corpus, source, runtime packages
and locks, durations, CPU topology/governor and thread environment. Duration overrides,
`--candidate-limit` and `--repeats` support smoke tests; release measurements use
the defaults above. Run `scripts/benchmarks/summarize_cpu.py /path/to/formula-sweep`
to create `native-summary.csv` and `measurement-summary.json`; the selection
`summary.json` and raw reports remain separate.

For supplemental whole-process HTTP CPU samples, run
`scripts/benchmarks/observe_worker_cpu.py` with the sweep controller PID and its
output directory. Its `summarize` subcommand records sample offsets, gaps and
clock resolution. Native reports use exact internal process CPU time; HTTP CPU
intervals are approximate because they are inferred from report publication time.

### Retained deployment evidence

The sections below document the **previous 2026-10-09 ONNX formula release**.
Its paths and throughput figures describe retained external rollback artifacts,
not the new Paddle worker. The current sweep uses the new reviewed corpus and
must not be presented as a like-for-like accuracy or speed comparison with the
four formula fixtures in the historical runs. New rollout requires the staged
verification and traffic-switch steps in the formula rollout guide.

The historical `formula_models_cpu.py` model-screen harness is local development
work outside this release. It requires `--root` to point to a retained prior
checkout containing `services/RapidLaTeXOCR` and its fixtures. Use the current
sweep and accuracy tools against this release; generated reports stay outside Git.

Deployed on **2026-10-09** to `chao@192.168.0.203`. Both production recognition
routes now reach this Ubuntu Server through the existing restricted reverse SSH
tunnels to `120.79.232.67`. All ten ONNX sessions use only
`CPUExecutionProvider`; model hashes are verified before initialization.

## Selected configuration for balanced demand

The host is an AMD Ryzen 7 5800H: eight physical cores, sixteen logical CPUs,
approximately 13 GiB RAM, Ubuntu 26.04.1 LTS, and kernel 7.0.0-38. On this host,
adjacent logical IDs are SMT siblings: `(0,1)`, `(2,3)`, …, `(14,15)`.

| Setting | Table | Formula |
| --- | --- | --- |
| Worker processes | 1 | 1 |
| ONNX intra-op threads per session | 7 | 1 |
| Logical CPU affinity | 0–13: seven physical cores | 14–15: one physical core |
| Local mutual TLS endpoint | `https://127.0.0.1:18080` | `https://127.0.0.1:18081` |
| API global / per-principal concurrency | 1 / 1 | 1 / 1 |

OpenCV, OpenMP, OpenBLAS, MKL, and NumExpr use one thread outside the configured
ONNX thread pools. The persistent CPU governor is `performance`; the original
`powersave` / `balance_performance` settings are saved for restoration. The
eight-core affinity layout is specific to this server; check `lscpu -e` before
reusing it elsewhere.

API policy **revision 13** changes table concurrency from 3 global / 2 per
principal to 1 / 1, matching the measured worker capacity. Formula remains 1 / 1.
Other policy fields retain revision 12 values. The existing gateway queue handles
overflow. The API image remains
`sha256:6853dddfa6c05d7af5ec4c3c2bfd2bc1e35d357de4e20bf011bffc9fa2d0b058`.

## Measured throughput

The deployed endpoint benchmark offered **one formula per table** for 90 seconds,
kept the table slot occupied, and waited for all offered formulas to finish.
Every request established a fresh TLS connection with CA, hostname, and client
certificate verification. Image validation, decoding, inference, postprocessing,
and response serialization are included. Startup and fixture warmup are excluded.

| Deployed service | Successful requests | Throughput | Mean HTTP latency | P95 HTTP latency |
| --- | ---: | ---: | ---: | ---: |
| Table | 66 | 0.726/s | 1.377 s | 1.705 s |
| Formula | 66 | 0.726/s | 0.971 s | 2.431 s |

The first measured pass completed **132 requests with zero errors** in 90.94 s:
**1.452 combined extractions/s**, approximately 87 combined extractions/minute.
The raw report is `deployed-http-balanced.json` in the deployment host's reports.
A sustained repeat completed another 66 requests per service with zero errors:
**0.724/s per service**, table P95 **1.709 s**, and formula P95 **2.441 s**.
Across the two deployed passes, **264 requests succeeded with zero errors**.
The two combined rates were **1.449–1.452 extractions/s**.
A comparison with one eight-thread table worker and one single-thread formula
worker, both sharing all cores, achieved only **0.659/s per service** over 91.01 s
with zero errors. The disjoint 7/1 split improved balanced throughput by about 10%.

The configuration sweep included 53 native inference runs, 2,175 timed successes,
and zero errors. Across the sweep and successful HTTP benchmarks, each of the
eight fixtures produced the same output SHA-256 in every configuration. Every
inspected model session reported CPU-only execution and the requested thread count.

Selected native results under simultaneous continuous saturation of both services:

| Core allocation and configuration | Table/s | Formula/s | Balanced capacity, slower service/s |
| --- | ---: | ---: | ---: |
| Shared cores: table 1×8 threads, formula 1×4 threads; stock power | 0.507 | 1.854 | 0.507 |
| Four cores each: table 1×4, formula 4×1; stock power | 0.422 | 3.526 | 0.422 |
| Six table / two formula cores: table 1×6, formula 2×1; stock power | 0.570 | 2.058 | 0.570 |
| Seven table / one formula core: table 1×7, formula 1×1; stock power | 0.647 | 1.113 | 0.647 |
| Seven table / one formula core: table 1×7, formula 1×1; performance power | 0.690 | 1.054 | 0.690 |

Saturated native results deliberately keep formula busy continuously; the balanced
HTTP workload lets it idle after finishing its share, so the two measurements
describe different loads. The performance governor raised table throughput by
about 6.6% in the 7/1 saturation comparison. Sustained load reached approximately
96°C Tctl, so cooling and ambient conditions can change capacity.

When run alone under stock power, the highest measured table result was
**0.787/s** with 1×8 threads; formula peaked at **7.148/s** with eight single-thread
workers. Those are separate service capacity measurements. The formula-heavy
configuration consumes cores needed by tables under balanced demand.

An experimental session profile enabled memory arenas/patterns and disabled
spinning. It did not improve balanced capacity enough to justify its
memory cost: table 1×8 used approximately 1.89 GiB peak worker RSS versus 0.71 GiB
for the existing session options, with slightly lower throughput. The unused
profile has been removed from the runtime; the deployment host's benchmark
reports retain historical evidence for the tuning decision.

These figures cover four table images (ruled, borderless, and a synthetic text
table) and four formula images, converted to lossless WebP. Formula generation
length affects latency substantially. P95 uses the benchmark's sorted-sample
floor quantile. HTTP latency measures each worker request and excludes time in
the driver's formula queue, public network transit, and gateway admission waits.
This is the best measured configuration for the supplied balanced workload,
rather than a capacity guarantee for every image size or formula length.

Benchmark reports are operational artifacts under `/home/chao/snowshot/reports`
on the deployment host. The summarizer produces `native-summary.csv` and
`summary.json` from the raw JSON files. Raw reports retain fixture dimensions/hashes,
providers, session thread counts, per-fixture results, elapsed times, errors,
and worker memory peaks.

## Installed layout and service management

| Path | Purpose |
| --- | --- |
| `/home/chao/snowshot/services/{TableStructureRec,RapidLaTeXOCR}` | Source and verified models |
| `/home/chao/snowshot/{table-venv,latex-venv}` | Separate hash-locked Python environments |
| `/home/chao/snowshot/runtime/cpython-3.12.15-linux-x86_64-gnu` | Python 3.12.15 standalone runtime |
| `/home/chao/snowshot/reports` | Benchmark and test evidence |
| `/etc/snowshot/{table,latex}-cpu.env` | Production worker settings |
| `/etc/snowshot/{pki-table,pki-latex,ssh}` | Worker certificates and restricted tunnel keys |
| `/etc/systemd/system/snowshot-cpu-worker@{table,latex}.service.d/cpu-affinity.conf` | CPU allocation |
| `/usr/local/libexec/snowshot-cpu-power` | Root-owned power policy helper |
| `/var/lib/snowshot/cpu-power.before` | Original power settings |

Enabled system services start at boot, independent of a Windows login:

```bash
sudo systemctl status snowshot-cpu-power \
  snowshot-cpu-worker@table snowshot-cpu-worker@latex \
  snowshot-cpu-tunnel@table snowshot-cpu-tunnel@latex --no-pager
sudo journalctl -u snowshot-cpu-worker@table -u snowshot-cpu-worker@latex \
  -u snowshot-cpu-tunnel@table -u snowshot-cpu-tunnel@latex --since '-10 minutes'
```

Worker failures restart automatically; tunnels reconnect automatically. Ports
18080 and 18081 bind loopback on both Ubuntu and the API host. The worker's client
CA rejects connections without an authorized client certificate. The API's
existing URLs remain `https://snowshot-table-rec:18080` and
`https://snowshot-latex-rec:18081`.

Dependencies use each service's `requirements-cpu.lock` with hash checking.
Table uses NumPy 2.2.6 / OpenCV 4.12.0.88; formula uses NumPy 1.26.4 /
OpenCV 4.11.0.86. Both use ONNX Runtime 1.24.4 and FastAPI/Uvicorn.
System libraries `libgl1` and `libglib2.0-0t64` are installed. Python 3.12 is
isolated from Ubuntu's system Python 3.14. Model archives and lock files have
saved SHA-256 evidence in the raw report directory.

## Reproduce installation and benchmarks

After staging the source, runtime, locked environments, verified model files,
and private worker configuration, install on this server with:

```bash
sudo bash /home/chao/snowshot/scripts/ubuntu/Install-UbuntuRecognition.sh \
  /home/chao/snowshot chao 1 7 1 1 \
  0,1,2,3,4,5,6,7,8,9,10,11,12,13 14,15 performance
```

The installer backs up configuration and units, installs workers and the power
policy, and leaves production tunnel activation as a separate step. The initial
backup is `/var/backups/snowshot-ubuntu203/20261009T152831Z`. Confirm authenticated
readiness before releasing the previous host's forwarding ports and enabling:

```bash
sudo systemctl enable --now snowshot-cpu-tunnel@table snowshot-cpu-tunnel@latex
```

For a native single-service measurement, use the corresponding environment:

```bash
cd /home/chao/snowshot
OMP_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 MKL_NUM_THREADS=1 \
  table-venv/bin/python scripts/benchmarks/recognition_cpu.py \
  --service table --root services/TableStructureRec --workers 1 --threads 7 \
  --affinity 0,1,2,3,4,5,6,7,8,9,10,11,12,13 --cycles 3 --output reports/recheck-table.json
```

For simultaneous saturation, keep competing benchmarks off the server and run:

```bash
OMP_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 MKL_NUM_THREADS=1 \
  bash scripts/benchmarks/run_mixed_cpu.sh recheck-mixed \
  1 7 0,1,2,3,4,5,6,7,8,9,10,11,12,13 1 1 14,15 90
```

`recognition_http.py` verifies equal offered demand against the deployed HTTPS
endpoints. It requires temporary API client certificates under
`probe-pki/{table,latex}-communication`; never commit these certificates or keys.
The benchmark client material was removed after the deployment checks. The
optional `--table-port` / `--latex-port` arguments support separate local test
endpoints. To regenerate compact results locally, first copy the raw JSON reports
from the deployment host into `reports/ubuntu-203-20261009/raw`, then run:

```powershell
python scripts/benchmarks/summarize_cpu.py reports/ubuntu-203-20261009
```

## Validation and API policy

The final Ubuntu suites passed **173 table tests and 52 formula tests**; each
suite skipped three Windows-only renderer tests. Windows configuration/renderer
checks passed separately (24 for each service), as did the table Redis ACL check
and 37 .NET domain tests. Deployment configuration validation and shell syntax
checks passed.

Through the actual API-host tunnels and client certificates, the synthetic table
returned HTML containing `Notebook` and formula 2 returned `x^{2}+y^{2}=1`.
Unauthenticated clients were rejected by both workers. API component health
reported table/formula healthy and configured/active policy revision 13 with
matching fingerprints. Public `https://snowshot.top/health/ready` returned ready.

The API host's active configuration is
`/opt/snowshot-v2/runtime/appsettings.Production.json`. The deployment helper
`scripts/ubuntu/Apply-UbuntuCpuPolicy.sh` merges policy 13 while retaining
non-policy settings, backs up configuration, and recreates only the API container.
The successful backup is
`/opt/snowshot-v2/backups/ubuntu-cpu-policy-20261009T154501Z`.

Policy activation is monotonic. Restoring revision 12 after activating 13 will
not roll policy state back. A policy rollback requires a new revision greater
than the active revision; the helper uses recovery revision 14 on failure.
Restoring just the previous forwarding host does not require a policy rollback.

## Restore the retained WSL deployment

The previous `SnowShot-Ubuntu-LTS` workers, models, certificates, and tunnels on
MAIN-PC remain available. Their services and the `SnowShotUbuntuWorkers` Windows
startup task are disabled. The earlier Windows installation at `.203` is no
longer the current rollback target because that address now runs Ubuntu Server.

From this repository on MAIN-PC:

```powershell
.\scripts\ubuntu\Restore-WslRecognition.ps1
```

The helper releases Ubuntu's production forwarding ports, enables the retained
WSL workers/tunnels, and restores the Windows startup task. Sudo may request
authentication. Verify both worker components and public readiness afterwards.
The conservative API revision 13 concurrency settings work with this rollback.

To restore the server's original power policy persistently:

```bash
sudo sed -i 's/ExecStart=\/usr\/local\/libexec\/snowshot-cpu-power performance/ExecStart=\/usr\/local\/libexec\/snowshot-cpu-power stock/' \
  /etc/systemd/system/snowshot-cpu-power.service
sudo systemctl daemon-reload
sudo systemctl restart snowshot-cpu-power
```
