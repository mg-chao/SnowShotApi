# SnowShot Operations Runbook

Use the alert's owner and runbook section as the starting point. Preserve
request IDs, trace IDs, timestamps, and the relevant metric window in the
incident record. Do not delete durable accounting or audit evidence while
mitigating an incident.

## Change and rollback

Confirm the deployed commit, configuration revision, and active policy revision
before changing production. Prefer a reversible configuration change or a
rollback to the last known-good commit. After the change, verify readiness,
policy convergence, and error counters from a fresh metric window. Roll back
again if replicas disagree on the policy fingerprint or reservations are being
rejected for a stale policy.

Temporary policy revision 5 raises the principal daily allowance to 6 CNY and
the daily operator budget to 100 CNY. Revision 6 restores those values to 3 CNY
and 50 CNY at the next Asia/Shanghai day boundary. Both revisions preserve the
500 CNY monthly operator budget and the 0.03 CNY maximum for every operation.
The production host mounts `runtime/appsettings.Production.json`; the restore
job runs `Restore-TemporaryBudgets.sh`, keeps a rollback copy, recreates only
the API container, and fails back if liveness does not recover.

## Readiness and dependency outage

Check the API readiness endpoint and inspect PostgreSQL, Redis, provider access,
and table-worker connectivity in that order. Confirm that the dependency is
reachable from the API network and that credentials, certificates, and clocks
are valid. Keep traffic admission closed when a required dependency is
unavailable; restore the dependency or fail over before reopening traffic.
`/health/live` is public and process-only. `/health/ready` is public and returns
only `ready` or `not_ready`; it requires policy convergence, at least 0.03 CNY
operator headroom, viable provider routes, and the table-worker mTLS probe.
Detailed `/health/components` output is available only over API loopback and is
not proxied by nginx.

Translation starts each batch on one of the configured logical models and
switches models only for retryable failures. Use the model/provider/access
identity on provider attempts to distinguish model degradation from an access
or network failure. The nginx API read timeout must remain greater than every
application execution deadline so the application can settle the operation and
return its structured timeout response.

Provider circuits are shared in Redis by logical model, provider, and access.
Five consecutive transient failures or a 50 percent failure ratio over at least
10 attempts opens an access. Authentication errors open it for at least 10
minutes. A half-open access admits one probe at a time and requires two valid
responses before closing; caller cancellation is not counted as a failure.

## Queue and lease incidents

Compare active leases, queue wait, stale evictions, and rejection counters with
the configured capacity. Stop a runaway producer or reduce concurrency before
increasing limits. For lost or fenced leases, identify the owner token and
fence transition in traces, then allow reconciliation to settle expired work.
Do not replay a request until its idempotency and settlement state are known.

## Reconciliation and cost

Inspect the reconciliation backlog age, outcomes, provider checkpoints, and
unknown-cost counters. Verify the operation, attempt, usage-event, and budget
rows in PostgreSQL before retrying a provider call. Unknown cost is settled at
the policy maximum until durable evidence is available; never overwrite the
original event to make totals appear correct.

## Identity, budgets, and retention

Treat identity-integrity conflicts and policy or budget mismatches as data
integrity incidents. Freeze destructive retention work, compare fingerprints
and policy revisions across replicas, and use the database-authored state as
the source of truth. Resume retention only after the conflict is resolved and
all referenced identities remain protected.

## Worker recovery

Verify the Windows service account, process restart count, model files, mTLS
certificate chain, and loopback listener. Confirm the reverse tunnel or
Tailscale route before changing firewall rules. Restart the worker through
WinSW, then verify `/health/ready` through the authenticated API path and check
that worker-busy and provider-access metrics return to baseline.
For the reverse SSH topology, the `SnowShotTableTunnel` scheduled task must stay
`Running`. Its persistent runner and rotated logs are under
`C:\ProgramData\SnowShot\ssh`; a `Ready` task with exit result 255 means the old
one-shot action is still installed or the runner itself failed.
