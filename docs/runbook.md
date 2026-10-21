# SnowShot Operations Runbook

Use the alert's owner and runbook section as the starting point. Preserve
request IDs, trace IDs, timestamps, and the relevant metric window in the
incident record. Do not delete durable accounting or audit evidence while
mitigating an incident.

## Change and rollback

The [20 October production release record](production-release-20261020.md)
records the active policy-15 API, immutable artifacts and retained recovery settings.

For API/recognition releases with an unchanged policy, use the
[blue/green deployment procedure](bluegreen-deployment.md). Policy-changing
releases require the separate maintenance procedure in that guide.

Policy revision **15** introduces the public `ai-model` (AI Model / AI 模型).
Both `/api/v1/chat/models` and `/api/v2/chat/models` expose only this model;
it is a general model (`translation: false`, `translation_mode: "default"`),
with vision and reasoning support. `qwen3.8-flash` and `qwen-mt-flash` remain
accepted request aliases. Text requests use `qwen3.7-flash`; an `image_url`
content part anywhere in the conversation selects `qwen3.8-flash`. Alias,
admission, and duplicate-request identity are resolved before reservation.
Response model fields retain the requested ID. Provider attempt identities
record the actual execution model. Reasoning defaults to disabled unless
explicitly requested.

The current candidate overlay is `deployment/policy/policy-revision-17.json`;
see the [screenshot translation admission update](translation-admission-20261022.md).
The policy-15 overlay is retained unchanged as a historical recovery reference.
Revision 14 is reserved by the historical CPU recovery script: never reuse it.
The deployed policy-15 AI Model uses 60 RPM, four concurrent requests per
principal, 64 globally, a 128-request queue, and the existing 30-second queue / 300-second execution
timeouts. Table and formula concurrency remains 1/1. Daily user allowance
remains ¥3, daily/monthly operator budgets ¥50/¥1,000, and each operation's
operator reservation maximum ¥0.03.

Deploy the backend first:

1. Record the deployed commit, database active revision, and effective configuration.
   Back up the mounted configuration securely. Apply the additive
   `AddModelPricingSnapshot` database migration before the new API starts; it adds
   nullable text to `usage_operations`, leaving historical scalar prices intact.
2. Run `python3 scripts/Prepare-AiModelPolicy.py --settings
   runtime/appsettings.Production.json --output runtime/appsettings.ai-model.candidate.json
   --active-revision <verified-revision>` (one command). It creates a new candidate
   and explicitly removes retired owned keys before installing revision 17.
   Review locally; the file may contain credentials. It does not deploy or print them.
   If revision 17 or higher is already active, supply `--revision` higher than both
   the active and configured revisions.
3. Remove obsolete owned keys from **every** additional JSON/environment layer,
   especially `Policy:Resources`, `Policy:Pricing`, `Providers:Models`, and the
   old `Providers:Translation:LogicalModels:1` value. .NET merges dictionaries and
   indexed arrays: adding an overlay does not remove old keys. Preserve cloud
   credentials and unrelated deployment settings. The new base settings must be
   deployed with the candidate. Validate with `Verify-DeploymentConfiguration.ps1`;
   for a later revision, pass `-ExpectedPolicyRevision <revision>`. Admission is
   compared with the selected `-PolicyReferencePath`, which defaults to the
   revision-17 overlay. The retained settings are independently compared with
   `-RestorePolicyReferencePath` (revision 15 by default). Select the matching
   reference explicitly when validating a historical profile or a new recovery
   configuration; a revision increase alone never changes its admission contract.
4. Replace the mounted settings with the reviewed candidate, deploy the API, and
   verify liveness, readiness, policy revision/fingerprint convergence, and errors.
   Check both catalog endpoints in en-US, zh-CN, and zh-TW. Send text and image
   requests with **each** old ID and `ai-model`: text must record the 3.7 route,
   images the 3.8 route, responses the requested ID. Verify legacy Qwen-MT options
   are translated to instructions and removed from upstream JSON. Use staging
   credentials for these provider-backed smoke checks; fixture tests alone cannot
   confirm regional upstream availability.
5. Release the desktop update after the compatibility checks. Saved selections
   migrate only when a successful catalog advertises `ai-model`. Offline/older
   backends preserve old selections; historical recognition output and drafts keep
   their original identities. Dedicated defaults and custom providers stay intact.

For recovery, prepare a configuration revision **higher than the active policy**,
not the historical revision 13/14 rollback overlays. Keep the additive database
column and all operations, attempts, usage events, and policy history. Prefer
retaining the AI Model alias-aware API when rolling back other changes; an older
API cannot serve desktop selections already migrated to `ai-model`. Any earlier
binary needs a higher-revision compatible policy and a separately reviewed client
compatibility plan. Do not run `Restore-TemporaryBudgets.sh` or
`Apply-UbuntuCpuPolicy.sh` against this rollout: their fixed historical revisions
are only evidence of earlier deployments.

### Token tariffs and settlement

Beijing/DashScope rates, CNY per million tokens (K = 1,000):

| Route / total input tokens | Input | Output | Cached input |
| --- | ---: | ---: | ---: |
| Qwen3.7, ≤32,000 | 0.20 | 0.80 | 0.04 |
| Qwen3.7, 32,001–256,000 | 0.60 | 2.40 | 0.12 |
| Qwen3.7, >256,000 within the supported context | 1.20 | 4.80 | 0.24 |
| Qwen3.8 | 0.80 | 2.70 | 0.10 |

Sources: [Qwen3.7](https://help.aliyun.com/zh/model-studio/qwen3-7-flash),
[Qwen3.8](https://help.aliyun.com/en/model-studio/qwen3-8-flash), and
[tier rules](https://help.aliyun.com/zh/model-studio/model-pricing).
The input count chooses one tier for the entire request, including completion.
Cached tokens are a subset of input; reasoning tokens are already included in
completion and are never charged twice. Rates are stored as integer NanoYuan
per token (e.g. ¥0.20/million = 200 NanoYuan/token).

A reservation snapshots its execution route and full immutable tariff. Routing
and tariff changes alter the policy fingerprint. AI chat deducts the successful
request's token cost from user allowance; operator cost includes all incurred
attempts. Dedicated translation retains the existing public character formula
(2,000 input / 8,000 output NanoYuan per character of the existing structured
protocol), with independent token-based provider costs. Missing/unusable usage
uses existing estimated/unknown settlement rules, never an exact token claim.
Old rows with a null pricing snapshot retain their original scalar interpretation.

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

Dedicated translation uses only `qwen3.7-flash` and shares its provider access
pool with text chat. Its structured JSON output protocol is unchanged. Chat
requests using legacy `translation_options` receive an ordinary translation
instruction preserving source/target languages, automatic detection, domain
hints, terminology, and translation memories. Native translation options and
`incremental_output` are stripped; ordinary conversations and image parts are
preserved when options are absent.

Text and vision have independent provider pools and circuits. Retries remain on
the selected route, including dedicated translation. Readiness checks both
underlying chat routes and the text translation route; there is no upstream
named `ai-model`. Inspect provider attempt identities for route-specific 429,
invalid-output, and circuit failures. A vision outage must not switch image
requests to a text model or Qwen-MT.

Image conversion clients use the same chat endpoint. Select a model whose
`supports_vision` is true in `/api/v2/chat/models`, then send the image as a
multimodal user message. The full JSON body, including base64, must fit within
2 MiB. For example (replace the abbreviated image with actual WebP base64):

```json
{
  "model": "ai-model",
  "stream": true,
  "enable_thinking": false,
  "temperature": 0,
  "max_tokens": 8192,
  "messages": [
    {"role": "system", "content": "Convert the image into semantic HTML. Return only an HTML fragment."},
    {"role": "user", "content": [
      {"type": "text", "text": "Convert this image faithfully."},
      {"type": "image_url", "image_url": {"url": "data:image/webp;base64,..."}}
    ]}
  ]
}
```

Markdown conversion changes the instruction to request GitHub-flavored Markdown.
Image content parts are forwarded intact; provider routing and streaming usage
options are applied as for other chat requests. A successful stream ends with
`[DONE]` only after settlement. Clients must also inspect provider finish reasons
and reject truncated or empty conversions. No conversion-specific deployment or
database migration is required.

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

For a blue/green deployment, first run `deploy.py status` and use the active
manifest's worker/tunnel unit names. Verify the selected private ports and avoid
restarting legacy fixed-port units. If the active worker release fails, use the
deployment controller's rollback so the API and its worker assignments recover
together. The fixed-name recovery examples below apply to the legacy layout.

The recognition capacity alert groups `snowshot.worker.busy` by resource.
Infrastructure routes table incidents to the Table worker owner and
`latex-extraction` incidents to the LaTeX worker owner (see the section below).

Verify the Windows service account, process restart count, model files, mTLS
certificate chain, and loopback listener. Confirm the reverse tunnel or
Tailscale route before changing firewall rules. Restart the worker through
WinSW, then verify `/health/ready` through the authenticated API path and check
that worker-busy and provider-access metrics return to baseline.
For the reverse SSH topology, the `SnowShotTableTunnel` scheduled task must stay
`Running`. Its persistent runner and rotated logs are under
`C:\ProgramData\SnowShot\ssh`; a `Ready` task with exit result 255 means the old
one-shot action is still installed or the runner itself failed.

## LaTeX worker recovery

Check `snowshot-cpu-worker@latex`, `snowshot-cpu-tunnel@latex`, and port 18081.
Verify the separate LaTeX certificate bundle and pinned PP-FormulaNet_plus-S
model hashes, then run the
authenticated readiness probe described in [LaTeX deployment](latex-deployment.md).
Inspect watchdog exit code 70, restart counts, and `snowshot.worker.busy` filtered
to resource `latex-extraction`. Check contention with table inference before
raising concurrency. Keep one worker process and the API's global concurrency
of one; tune CPU threads and affinity using the balanced benchmark sweep.
Never include uploaded images or recognized formulas in incident logs.
