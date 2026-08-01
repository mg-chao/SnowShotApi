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

## Readiness and dependency outage

Check the API readiness endpoint and inspect PostgreSQL, Redis, provider access,
and table-worker connectivity in that order. Confirm that the dependency is
reachable from the API network and that credentials, certificates, and clocks
are valid. Keep traffic admission closed when a required dependency is
unavailable; restore the dependency or fail over before reopening traffic.

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
