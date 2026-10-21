# Production recovery and latest API release — 20 October 2026

Production now serves `policy-15-626bee7` on API loopback port 5000. Its
configured and database-active policy are revision **15**, fingerprint:

```text
2a2b5d80d94458ab769c9e57592707ca7b849518b5a40da65531b61bc02dd0a8
```

The deployed application image is immutable:

```text
sha256:93605cd23d1485773595879b559fdced07dd50d597cde5838622e90abc132494
```

It was built off production from `5e67b51`. HEAD `626bee7` changes only the
deployment agent's configuration-copy permissions; its application and migrator
sources are identical. The installed API deployment agent matches HEAD's SHA-256
`e74f15d0b2b7404e520ec0c621696807c033f01d89ca72c2bca09ee11d5bc1c0`.

## Recovery and rollout

At incident inspection, nginx was left in maintenance mode and the active API
container had exited cleanly after a Docker stop, without OOM. PostgreSQL,
Redis, recognition workers and reverse tunnels remained running. The database
still had policy 13. Restarting the verified API and restoring the saved nginx
configuration recovered public readiness at approximately **19:39:49**.

The nullable `ModelPricingSnapshot` migration was then applied using the separate
migrator credentials while the old API continued serving. Both policy-15
candidate and policy-16 recovery settings passed the network-disabled deployment
description command. The candidate's effective configuration was frozen, with
the existing worker assignments and certificates retained.

The policy change used the documented maintenance procedure: close admission,
wait for the previous nginx workers to exit, stop the drained old API, start the
new API, require three successful component checks, then restore ingress.
Maintenance started at **19:41:58**; public readiness was verified by
**19:42:26**, an upper bound of **28 seconds**. Times are Asia/Shanghai.

The table/formula workers and their reverse forwards remain on loopback
28080/28081. All four systemd units are active, enabled and have zero restarts.
The API has zero restarts and uses `unless-stopped`. PostgreSQL, Redis and
telemetry were not recreated.

## Validation

- 47 affected .NET AI Model, deployment-description and host-configuration tests
  passed.
- Both catalog versions advertise only `ai-model` in en-US, zh-CN and zh-TW.
- `ai-model`, `qwen3.8-flash` and `qwen-mt-flash` each completed real text and
  image SSE requests with `[DONE]` and preserved the requested response model ID.
- Public table, formula and dedicated translation probes returned successful,
  matching/nonempty results.
- An accounting snapshot after activation showed 86 text-route and three
  vision-route operations, all settled with exactly one usage event each.
- All readiness components passed, with policy revision/fingerprint convergence,
  no API restart, and more than 256 MiB available-memory reserve.
- Five-minute public observation from 19:43:00 to 19:48:00 completed with
  **58 successful probes and zero failures**. Cleanup found no unfinished drain;
  shared workers remained active and old API processes remained stopped.

## Retained recovery material

Protected evidence and progress are on the API host under
`/opt/snowshot-v2/bluegreen/maintenance-5e67b51`; release manifests and frozen
configuration remain under `/opt/snowshot-v2/bluegreen/releases`.

The previous policy-13 API is stopped and retained. Its configuration must not
be restarted as an ordinary rollback after policy activation advanced to 15.
The reviewed `recovery-16-626bee7` configuration uses the same compatible API
image and fingerprint at revision 16; use the policy-maintenance recovery
procedure if needed. Database migrations are never reversed during recovery.
