# Production blue/green adoption — 20 October 2026

Completed production adoption and the forward → rollback → forward rehearsal.
The active release is `green-20261020-c`. The retained baseline is
`baseline-20261020`; its API container, worker units and tunnels are stopped,
with their source, images, models and protected configuration retained.

## Production result

| Component | Active listener | State |
| --- | --- | --- |
| API | `127.0.0.1:5001` | Running, zero restarts |
| Table worker and reverse forward | `127.0.0.1:28080` | Active, enabled for boot, zero restarts |
| Formula worker and reverse forward | `127.0.0.1:28081` | Active, enabled for boot, zero restarts |
| Baseline API | Former port 5000 | Stopped with exit code 0; container retained |
| Baseline workers/tunnels | Former ports 18080/18081 | Inactive and disabled; artifacts retained |

The API image remains the deployed `1d21ac2` binary:

```text
sha256:6853dddfa6c05d7af5ec4c3c2bfd2bc1e35d357de4e20bf011bffc9fa2d0b058
```

Configured and database-active policy remain revision **13**, fingerprint:

```text
c5aeb5a7f09c99d86c24aecd8b61622401fc3899bf465f6700f1860ece4f4419
```

Worker code, isolated Python runtimes, verified models, CPU affinity/thread
settings and certificate identities were reused. No database migration or
policy-15 application release was applied. PostgreSQL, Redis and telemetry
services were not recreated.

## Rehearsal and acceptance evidence

Times are Asia/Shanghai (UTC+08:00), first observed by public polling about
every two seconds.

| Transition | Observed time | Observation |
| --- | --- | --- |
| Baseline → green | 18:38:12 | Five minutes passed |
| Green → baseline rollback | 18:43:33 | Five minutes passed |
| Baseline → final green | 18:50:04 | Five minutes passed |

- **1,064 successful public readiness checks, zero failures**, from 18:26:01
  through 19:03:45, covering staging, all three transitions and cleanup.
- Public table and formula requests matched the reviewed fixtures on each side
  of the rehearsal: six successful extraction requests total.
- Public streaming chat through the final release returned the expected text
  and completed with `[DONE]` using the existing `qwen3.8-flash` model.
- Both model-catalog responses were identical across forward, rollback and final
  phases. Catalog SHA-256 values: v1 `eed5cd5f78ce1723fa1bc66efddb13924a81ab6058ec80d34bf76785b238b454`;
  v2 `8b42327a0eb51d93a369d33830bdea258880800dfc8db9a1965fd7db01bd5f8e`.
- Authenticated worker readiness/recognition passed locally and through both
  reverse forwards. TLS 1.2 and 1.3 rejected missing client certificates, with
  authenticated controls before and after each negative probe.
- After cleanup, the stopped baseline was restarted and verified privately
  without changing public routing, then stopped again. This exercised recovery
  from retained artifacts as well as the earlier warm rollback.
- Final persisted state has no pending operation. Both installed deployment
  agents match the repository source SHA-256
  `77c11afc33513b389f30e01bd9165f8c81ad8f6d3e1c9a7e87307bf03f49d675`.

Two initial candidates were rejected and retired before traffic switched.
Staging exposed launch-directory permissions under a private umask and missing
UID/GID preservation when copying bind-mounted secrets. Both were corrected and
covered by Linux regression tests; frozen snapshot contents were verified before
repairing their ownership metadata. Configuration identities also now ignore
Docker's nondeterministic mount ordering.

Raw operational evidence remains private under the local `.codex/bluegreen`
directory and the API host's `/opt/snowshot-v2/bluegreen` state/snapshot directory.
The worker host stores protected launch specifications under
`/var/lib/snowshot-deploy`. Worker administration uses the scoped passwordless
deployment helper.

## Repository validation

- 33 deployment tests passed in the Linux container, including lock contention,
  interruption recovery, policy mismatch, failed startup/tunnels, resource
  refusal, shared-worker retirement, bind ownership and private-umask behavior.
- 15 targeted .NET deployment-description/host-configuration tests passed.
- Target-host Linux worker checks passed: seven formula lifecycle/mTLS tests
  and three table mTLS/inference-gate tests.
- The real nginx/API/PostgreSQL/Redis harness passed a forty-second stream and
  queued work across switching, worker routing, rollback, and seven exactly-once
  accounting settlements with no reserved balances left behind.
- Targeted .NET formatting, deployment configuration verification, Python
  compilation and `git diff --check` passed. The unrelated full suite was not run.

The local Windows table mTLS smoke test failed its concurrent busy-response
assertion (404 instead of 503, with a request timeout during teardown). Its Linux
equivalent passed on the deployment host. No recognition-service source was
changed as part of this deployment work.

For subsequent releases and recovery, use the
[deployment guide](bluegreen-deployment.md), including its policy-changing
maintenance procedure. Historical fixed-port restart scripts describe the
retained legacy layout.
