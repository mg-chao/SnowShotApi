# Production release — 22 October 2026

Production serves commit `c13a440c45a4ea14ea469416eb0a5a9d566f93be` as
`policy-17-c13a440` on API loopback port **5001**. Configured and database-active
policy are revision **17**, with fingerprint:

```text
3c8aeb51e2771184c1ed6f7d02de8882bb9da2036b66ddfa69f9b0cc14b1d4c5
```

The immutable application image is:

```text
sha256:56cc69613993b73b881ca9ba57dbbe3733f7932c45fbfa8fb32c7af98650e831
```

It was built on the workstation using the pinned Dockerfile, transferred with a
verified archive SHA-256, and loaded on the API host. The installed deployment
agent matches the repository source. No database migration was needed.

## Result

AI Model admission is now **600 requests/minute per principal**, eight concurrent
and eight queued requests per principal. Global concurrency remains 64 and
the global queue remains 128. Prices, allowances, operator budgets, dedicated
translation admission, and CPU recognition admission retain their previous values.

Table and formula service code has not changed since the previous deployment.
Their existing verified workers and reverse tunnels remain active on loopback
28080/28081, enabled at boot, with zero restarts. Their source, Python runtimes,
models, CPU settings, and certificates were retained. PostgreSQL, all three Redis
containers, and telemetry retain their existing container identities.

## Maintenance

All times below are Asia/Shanghai on 22 October 2026.

| Event | Time |
| --- | --- |
| API admission closed with 503 and Retry-After | 01:09:08.921 |
| Previous nginx workers finished draining | 01:09:55.310 |
| Candidate passed three component checks | 01:10:38.760 |
| Public readiness and release identity confirmed | 01:10:40.101 |

Maintenance lasted **91.18 seconds**. An older nginx worker was already shutting
down and retained one client connection. It completed naturally; no nginx worker
or client connection was forcibly terminated. During investigation, the operator
interrupted the deployment controller just after it had completed draining,
stopped the old API, and started the candidate. Persisted state was inspected,
the candidate was verified again, and ingress was restored. Final state has no
pending operation. The old API exited with code 0 and remains retained.

## Verification

- 122 focused .NET AI Model, public-contract, deployment-description, and host
  configuration tests passed; 18 policy/configuration Python tests passed.
- 33 Linux deployment-controller tests and three harness-policy tests passed.
- The real nginx/API/PostgreSQL/Redis deployment harness passed streamed and
  queued work across switching, worker routing, rollback, and seven exactly-once
  settlements with no remaining reservations.
- Targeted .NET formatting, deployment configuration validation, and
  `git diff --check` passed. The full unrelated test suite was not run.
- Frozen candidate and recovery settings passed `--describe-deployment` with
  networking disabled before maintenance. The candidate fingerprint matches
  the reviewed revision-17 overlay exactly.
- Both workers passed authenticated readiness and real fixture recognition,
  directly and through the API-host tunnels, and rejected clients without a
  certificate before the rollout.
- Both public model catalogs advertise only `ai-model` in en-US, zh-CN, and
  zh-TW. `ai-model`, `qwen3.8-flash`, and `qwen-mt-flash` each completed real
  text and image SSE probes with `[DONE]` and the requested response model ID.
- Public table and formula fixtures matched; dedicated translation returned a
  nonempty successful result. An initial revision-17 accounting snapshot had
  21 operations, all committed with exactly one usage event, none pending, and
  zero upstream failures or HTTP 429 responses.

- Five-minute observation from **01:10:50 to 01:15:53** passed **57 checks with
  zero failures**. Each check verified public readiness/release identity,
  private component readiness, policy convergence, and the 256 MiB available
  memory reserve.
- After observation, both workers again passed direct and tunneled mTLS fixture
  probes and negative client-certificate checks. Cleanup completed without
  pending drains; all previous API releases are stopped and shared workers
  remain active.
- Final API inspection showed zero restarts, no OOM kill, and **761 MiB**
  available host memory. Persisted active/routed release agree with no pending
  deployment operation.
- The final accounting snapshot contained **58 operations, all committed**, no
  pending or overdue operations, exactly one usage event per completed
  operation, and zero upstream failures or HTTP 429 responses.

Private raw evidence is under local `.codex/production-c13a440` and the API
host's `/opt/snowshot-v2/bluegreen/maintenance-c13a440`.

## Recovery

The previous policy-15 API is stopped and retained. **Do not reactivate a lower
policy revision or use ordinary blue/green rollback for this policy change.**

The separately frozen `recovery-18-c13a440` configuration uses the previously
deployed compatible API image and the historical policy-15 admission profile
at revision **18**, with fingerprint:

```text
2a2b5d80d94458ab769c9e57592707ca7b849518b5a40da65531b61bc02dd0a8
```

Its offline validation passed. Source settings, template, Compose configuration,
and description result remain in the root-only release directory
`/opt/snowshot-v2/bluegreen/releases/recovery-18-c13a440`. Recovery must use the
policy-changing maintenance procedure: close admission, drain prior requests,
stop the old API, start the higher-revision recovery API, verify dependencies
and convergence, then reopen ingress. Retain all accounting and policy history.
