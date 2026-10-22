# API and recognition release switchover

Run `scripts/deployment/deploy.py` with Python 3.12+ and OpenSSH from a machine
that can reach both production hosts. Docker images are built off the API host
and loaded there before staging. No registry or additional load balancer is
required. This procedure leaves database, Redis and telemetry services running.

## Install and adopt

Copy `deployment/bluegreen-target.example.json` to a private local target file.
Set both SSH destinations, the worker account/release root, and the nginx files
that currently proxy this API. Fixture paths are relative to that target file.
Use pinned SSH host keys. Keep credentials outside target/release manifests.

```powershell
python scripts/deployment/deploy.py --target .secrets/bluegreen-target.json bootstrap
python scripts/deployment/deploy.py --target .secrets/bluegreen-target.json adopt baseline-20261020
```

Bootstrap prompts for sudo once. It installs root-owned agents, systemd templates
and a sudoers entry allowing only the worker deployment helper with no command
arguments. Worker source executes as the existing unprivileged account. The
helper accepts validated deployment operations, not arbitrary shell commands.
Only the Ubuntu tunnel keys gain their second, loopback-only permitted listener;
Windows recovery keys and existing forwarding stay unchanged.

API bootstrap also installs SSH liveness checks for the root account used by the
recognition tunnels: `ClientAliveInterval 30` and `ClientAliveCountMax 3`.
The installer validates both SSH syntax and the effective root configuration
before reloading SSH, and restores the previous drop-in if validation or reload
fails. Existing SSH sessions retain their original settings until reconnected.
The worker already probes the server every 30 seconds and systemd retries failed
tunnels every three seconds. Server-side checks are also required: after a local
network outage, a stale server session can otherwise retain its forwarding port
and reject every replacement connection with `remote port forwarding failed`.

For this incident, verify the listener belongs to the disconnected recognition
session before terminating that specific SSH child; do not stop the SSH daemon.
Let the existing worker units reconnect, then verify public readiness and both
authenticated recognition fixtures. An isolated network-blackhole check on
22 October 2026 reclaimed the lost session's port in 120.7 seconds and verified
that a replacement tunnel could bind it. Recognition remains unavailable while
the worker has no power or network.

Adoption snapshots effective API environment/bind mounts under a root-only
release directory, records worker source/model/runtime hashes, backs up ingress
files, and introduces a managed upstream pointing to the existing API. Existing
containers and workers are not restarted. The API host persists manifests,
progress and events in `/opt/snowshot-v2/bluegreen/state.json`; private snapshots
must never be added to Git or copied into public reports.

## Stage and switch

For the initial rehearsal, copy `deployment/bluegreen-release.example.json` and
give it a unique release ID. `legacy_clone` accepts the exact adopted image and
effective configuration, changing only listeners and worker URLs. It cannot be
combined with an image/configuration change. Source releases are reused for
worker clones; their source, models and isolated Python environments are hashed
again before startup.

```powershell
python scripts/deployment/deploy.py --target .secrets/bluegreen-target.json stage deployment/bluegreen-release.example.json
python scripts/deployment/deploy.py --target .secrets/bluegreen-target.json switch first-green
python scripts/deployment/deploy.py --target .secrets/bluegreen-target.json status
python scripts/deployment/deploy.py --target .secrets/bluegreen-target.json cleanup
```

API slots use 5000/5001, table slots 18080/28080, and formula slots 18081/28081.
All are loopback-only. A release that omits a worker keeps the active worker
assignment. Each new worker has a distinct immutable launch specification and
systemd instance, even when its slot has been used before. Stage never replaces
an occupied listener. Active worker and tunnel units are enabled for boot;
retiring them also disables their startup links. mTLS checks verify readiness, a reviewed recognition
fixture, and TLS rejection without a client certificate, on the worker and
through the API-host reverse tunnel. Temporary decrypted probe keys are removed.

Before starting candidates, allow at least 384 MiB extra API memory and 256 MiB
reserve; workers require the previous instance's observed memory plus a 1 GiB
reserve. Both hosts need 512 MiB free disk for staging. During warmup and
observation the reserves remain mandatory. These are refusal thresholds, not
capacity guarantees; do not bypass them on the small production API host.

For future API images, use an immutable image ID/digest:

```json
{
  "id": "api-release-abc123",
  "api": {
    "image": "sha256:<loaded-image-id>",
    "settings_file": "/secure/staging/appsettings.Production.json",
    "environment_file": "/secure/staging/api-overrides.env"
  },
  "workers": {
    "latex": {
      "runner": "/home/chao/snowshot/releases/abc123/run-formula",
      "environment_files": ["/etc/snowshot/latex-abc123.env"]
    }
  }
}
```

Settings/environment overrides are optional server-side paths, copied into the
private candidate snapshot. Environment overrides also update existing
key-per-file secrets because mounted secrets take precedence. A staged worker
runner must belong to a complete release containing `services/<service>`, its
verified models and an isolated runtime. Preserve existing inference thread
counts, affinity and watchdog settings unless separately benchmarked.

The API's `--describe-deployment` command loads and validates effective settings
without serving HTTP, starting hosted services or accessing dependencies.
Staging runs it with networking disabled and compares its policy revision and
fingerprint with production **before starting the API**. Different policies are
refused. Images predating this command are accepted only as exact legacy clones.

Explicitly reviewed additive migrations can be requested in the release manifest:

```json
"migration": {
  "image": "sha256:<loaded-migrator-image-id>",
  "backward_compatible": true,
  "secrets_directory": "/secure/migrator-secrets"
}
```

The migrator receives its separate mounted credentials. It runs after policy
preflight and before candidate startup. Confirm old and new binaries tolerate
the resulting schema; this flag records the operator's review, not an automated
compatibility assessment. Database migrations are never reversed by rollback.

## Drain, rollback and interruption

Switch validates nginx, replaces only its managed upstream, and reloads. Every
API proxy uses that upstream and disables proxy retries. Health responses identify
the release with `X-Snowshot-Release`; application response bodies are unchanged.
Old nginx processes retain existing requests, including queued work and SSE.
The controller verifies the release and readiness for five minutes. Failure or
an explicit interruption during observation restores the previous ready release
and drains the failed candidate before retiring its processes.

Cleanup waits for the recorded nginx PIDs **and their process start times** to
disappear before stopping the inactive API. It then stops tunnels/workers not
referenced by any running API. A 360-second drain deadline never triggers forced
termination: overdue processes remain running and cleanup reports that state.
API stop grace is 390 seconds; future API builds allow 360 seconds for host
shutdown. Recognition retains its 60-second supervisor stop bound.

```powershell
python scripts/deployment/deploy.py --target .secrets/bluegreen-target.json rollback
```

Rollback restarts and verifies stopped prior components if necessary, then uses
the same switch and observation sequence. Source/model/configuration artifacts
remain available after cleanup. Legacy reactivation refuses configuration drift.
Do not delete a retained release until its recovery value has been reviewed.

Both agents hold server-side locks for the controller session. After SSH loss,
run `status` before retrying. It compares the live nginx release header with
persisted state and records an interrupted switch recovery. If readiness fails
and routing is uncertain, preserve both releases, inspect nginx and journals,
and restore the last verified upstream before cleanup. Never reclaim a slot
by killing an unknown listener. A preparation that failed before registration
may leave a private artifact directory; use a fresh release ID and inspect it
before removing anything manually.

## Policy-changing maintenance

Production adoption deliberately retains the live policy 13 and application
versions. The repository's policy 15 needs a separate reviewed release and the
AI Model compatibility checks in `runbook.md`.

1. Prepare both the candidate settings and a recovery configuration with a
   revision higher than the candidate. Validate aliases, accounting compatibility
   and layered-key removal. Back up configuration and record active policy state.
2. Put API proxy locations and public readiness into maintenance: return 503 with
   `Retry-After`, leaving static downloads and worker tunnels running. Reload nginx.
3. Wait for pre-maintenance nginx workers to finish. A just-started request can
   require its queue wait, execution deadline and settlement time. Do not start a
   policy-changing API while the old instance can still admit requests.
4. Apply reviewed compatible migrations, start the candidate with its new policy,
   and verify policy convergence, dependencies and client compatibility privately.
5. Reopen ingress only after checks pass. If policy activation advanced but the
   release failed, keep maintenance active and use the higher-revision recovery
   settings with a compatible binary. Never reactivate a lower policy revision.

## Focused verification

```powershell
python -m unittest discover -s scripts/deployment/tests -v
dotnet test tests/SnowShotApi.Tests/SnowShotApi.Tests.csproj -c Release --filter "FullyQualifiedName~DeploymentDescriptionTests|FullyQualifiedName~HostConfigurationTests"
python tests/deployment/run_harness.py
```

The isolated harness uses real nginx/API/PostgreSQL/Redis, deterministic provider
and worker doubles, a forty-second stream, queued and post-switch work, worker
routing, rollback and cleanup. It checks exactly-once dispatch/settlement,
aggregates and released reservations. Existing recognition mTLS/lifecycle tests
and real-model fixture probes cover native workers. The harness removes only
its own Compose project/volumes; set `SNOWSHOT_KEEP_DEPLOYMENT_STACK=1` to inspect
failed runs. Do not run the unrelated full test suite for this change.
