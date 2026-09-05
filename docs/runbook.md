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

Historical policy revision 5 temporarily raised the principal daily allowance
to 6 CNY and the daily operator budget to 100 CNY. Revision 6 restored those
values to 3 CNY and 50 CNY. Revision 7 retains those daily limits and raises
the monthly operator budget from 500 CNY to 1000 CNY. Revision 8 preserves
every allowance, budget, and operation maximum while retiring the qwen-flash,
qwen-plus, and deepseek-v4-flash models. Revision 9 preserves every limit and
price and adds the cost estimation ratios
(3 payload bytes per input token, 2 delivered characters per output token)
used to settle abnormal operations from local evidence. The chat model list is
served by qwen3.8-flash, qwen3-vl-flash, and qwen-mt-flash. The legacy
`/api/v1/chat/models` response remains available, while `/api/v2/chat/models`
exposes `supports_reasoning`, `translation_mode`, and `supports_vision`.
Translation traffic is split evenly between qwen3.8-flash and qwen-mt-flash.
Admission limits were
realigned to the Alibaba Cloud Model Studio lowest spending tier (qwen3.8-flash
and qwen3-vl-flash: 60 requests per minute per principal and 64 global
concurrent slots; qwen-mt-flash and translation: 30 requests per minute per
principal). Revision 10 keeps those limits and updates the qwen3.8-flash price
to the current Model Studio rate - 0.8 CNY per million input tokens and 2.7 CNY
per million output tokens (800/2700 NanoYuan per token) after the 2026-08-27
price reduction - while every allowance, budget, and operation maximum stays
unchanged. The production host mounts
`runtime/appsettings.Production.json`; the policy deployment job runs
`Restore-TemporaryBudgets.sh`, keeps a rollback copy, recreates only the API
container, and fails back if liveness does not recover.

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

Translation is split evenly between the qwen3.8-flash and qwen-mt-flash
logical models: the operation ID hash picks the initial model and retries
alternate to the other, so qwen-mt-flash's 60 RPM upstream tier no longer
bottlenecks the service. Models listed under
`Providers:Translation:LogicalModels` also appear in the chat model catalog
flagged as `translation: true`. The chat provider merges system prompts into
the first user turn only for models configured with `MergesSystemIntoUser`
(qwen-mt-flash), whose upstream rejects the system role; qwen3.8-flash keeps
system messages unchanged. Watch `provider_http_4xx` and `invalid_output`
outcomes on the qwen3.8-flash translation attempts after enabling the split,
and 429s on qwen-mt-flash while its upstream tier stays at 60 RPM.

The two translation models use different upstream wire contracts. Models
configured with `NativeTranslationOptions` (qwen-mt-flash) follow the Alibaba
Cloud Model Studio Qwen-MT convention: the item text is the single user
message verbatim, language and domain control ride in the native top-level
`translation_options` field (`source_lang`/`target_lang`, with `zh-CHS` and
`zh-CHT` mapped to `zh` and `zh_tw`, and every non-general domain rendered as
an English domain prompt), and the completion content is the translation
itself. Unflagged models (qwen3.8-flash) keep the JSON-envelope instruction
protocol with `response_format`. The vendor caps Qwen-MT input at 8,192
tokens per call, supports single-turn user messages only, and documents that
`translation_options` overrides equivalent prompt instructions; the glossary
and translation-memory hooks (`terms`/`tm_list`) stay unused until the public
contract carries them. With the native contract, `invalid_output` on
qwen-mt-flash can only mean a null or empty completion, not a malformed JSON
envelope. Chat clients that call qwen-mt-flash directly can pass
`translation_options` in their own request body — the chat path forwards
unknown fields verbatim.

Chat and
translation share the same provider access pool, so
use the model/provider/access identity on provider attempts to distinguish
model degradation from an access or network failure. The nginx API read
timeout must remain greater than every application execution deadline so the
application can settle the operation and return its structured timeout
response.

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

Inspect the reconciliation backlog age, outcomes, provider checkpoints, and the
unknown-cost and estimated-cost counters. Verify the operation, attempt, usage-event,
and budget rows in PostgreSQL before retrying a provider call. Operator cost settles
gradually from durable evidence: exact usage first, then an estimate recorded on the
attempt when a dispatched request never returned usable usage (truncated streams bill
the transmitted payload plus delivered content; provider 408/5xx bill the transmitted
payload or characters; rejected 4xx responses bill zero), and only the truly unevidenced
cases — unresolved dispatch, crash, abandoned preparation — fall back to the policy
maximum. Estimation ratios live in the fingerprinted policy (`Policy:Estimation`).
Estimated and capped settlements are clamped to the per-operation operator maximum and
never qualify for verifiable overage. Never overwrite the original event to make totals
appear correct.

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
