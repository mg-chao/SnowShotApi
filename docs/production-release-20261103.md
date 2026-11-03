# Production release - 3 November 2026

Production serves commit `8954efb9d5f0b6ae142dcc85570e84cb8d632c34` as
`prod-20261103-payments-on`. The API uses loopback port **5001**, table recognition
uses **18080**, and LaTeX recognition uses **18081**. The initial release,
`prod-20261103-8954efb-b` on port **5000**, upgraded all three components from
separately retained source, runtime, model and configuration artifacts through
the blue/green deployment controller. The subsequent owner-requested payment
activation changes only the two purchase gates and the API slot.

The API image is:

```text
sha256:4e69718f1271f755c1b0bda91c213c657955d1549c6d1a76450e28026b403dc2
```

The migrator image is:

```text
sha256:5c171cc1228857594510208e7801ed9cb16854862120ca2419888e8a902a3e16
```

Both images were built on the workstation with the pinned Dockerfile, then
transferred and verified by archive SHA-256. The installed API deployment agent
matches the repository. Policy revision **17** retains fingerprint
`3c8aeb51e2771184c1ed6f7d02de8882bb9da2036b66ddfa69f9b0cc14b1d4c5`.
Existing prices, admission limits, provider configuration, worker thread counts
and CPU affinity are retained.

## Accounts and payment configuration

The owner-supplied production virtual-payment AppKey is configured privately,
with `Environment=0`, `AllowPaymentTesting=false`, release Mini Program codes
and path checks enabled. The key is omitted from this record and source control.
The production snapshot contains the existing Mini Program credentials,
encrypted-message settings, five standard product mappings and a dedicated,
persistent production account encryption key. The development test product is
not exposed.

The initial rollout retained `PaymentsEnabled=false` and
`ReleasePrerequisitesConfirmed=false`. Its account rollout record left filing,
published-product confirmation and real-payment acceptance outstanding; deploying
the production key did not assert completion of these prerequisites. Initial
package reads exposed five standard packages and `payments_available:false`.
The subsequent owner-requested activation is recorded below.

The production database previously had five migrations and no account tables
or account reconciliation instances. A private full PostgreSQL custom-format
backup completed before migration. Nine account migrations, through
`20261102150902_AddCheckoutRecoveryAndEventRouting`, were applied before starting
the candidate API. New columns retain defaults compatible with the previous
anonymous API. Membership backfill had no existing account balances to convert;
there were no previous account financial writers to mix with the new version.
Runtime grants cover all account tables. No down migrations were applied.

## Initial release verification

- 126 focused .NET configuration, payment-platform, public-contract, deployment
  and image-format tests passed. Three local real-worker cases were skipped;
  actual production workers were exercised separately.
- 93 account, recovery, deletion, checkout, wallet and membership-migration
  integration tests passed against a separate temporary PostgreSQL instance.
  That instance was removed after testing.
- 58 targeted table/LaTeX interface and image-format tests passed in the staged
  Ubuntu runtimes. Production dependency locks matched the retained releases.
- 35 deployment-controller tests passed; four POSIX/root-specific cases were
  skipped on Windows. The unrelated full test suite was not run.
- Both workers passed authenticated readiness, reviewed real-model recognition,
  TLS 1.2/1.3 client-certificate rejection and authenticated control checks,
  directly on Ubuntu and through the API-host reverse tunnels before switching.
- Public table and LaTeX endpoints each recognized WebP, PNG and JPEG fixtures
  correctly after switching. A real AI Model SSE request completed with `[DONE]`.
- Public readiness, both model catalogs and the package catalog returned 200.
  The loaded production key matched the supplied value. Signed message callback
  GET verification returned the exact echo. No real payment was performed.
- Configured and database-active policy remain revision 17. All private health
  components are ready, account reconciliation reports no errors, and API and
  worker restart counts are zero, with no API OOM kill.
- PostgreSQL, all three Redis containers and telemetry retain their previous
  container identities and startup times.
- The five-minute production observation passed without a failed readiness,
  release-identity, policy-convergence or memory-reserve check. Cleanup stopped
  the previous API and unreferenced workers after nginx drained. All retained
  artifacts remain available. Final API-host available memory was **717 MiB**;
  active and routed release agree, with no pending deployment operation.

Private manifests, backup and evidence are under
`/opt/snowshot-v2/bluegreen/staging/prod-20261103-8954efb-b` and its initial
`prod-20261103-8954efb` staging directory. Local deployment artifacts are under
`.secrets/deployment/prod-20261103`. Worker source, copied models and isolated
runtimes are retained under
`/home/chao/snowshot/releases/prod-20261103-8954efb`.

## Production purchase activation

After updating the Mini Program API domain, the owner explicitly requested
enabling production purchasing. The blue/green controller deployed
`prod-20261103-payments-on` using the same API image and existing recognition
workers. The configuration comparison allowed only the API listener change and
`Accounts:Wechat:PaymentsEnabled=true` /
`Accounts:Wechat:ReleasePrerequisitesConfirmed=true`. No credentials, prices,
policy, schema or worker assignments changed. Production retains `Environment=0`,
`AllowPaymentTesting=false`, release codes and path checks.

Candidate startup configuration validation, both workers' local and tunneled
mTLS/recognition probes, private health components and signed callback verification
passed before switching. Public readiness identifies the new release; the public
package endpoint returns HTTP 200, five standard packages and
`payments_available:true`. Unauthenticated purchase requests return
`401 invalid_session`. No real payment or authenticated checkout was performed.

The five-minute readiness, release-identity, policy and memory observation passed.
Cleanup stopped the previous API after nginx drained while retaining its artifacts.
The final check at **11:33 China Standard Time** found all health components ready,
no API restarts or OOM kill, policy revision 17 converged, no pending operation,
and **684 MiB** available memory. PostgreSQL, Redis and telemetry container
identities and startup times are unchanged. Both recognition workers and tunnels
are active. One EF transaction error entry without exception details was present;
no unhandled API or account-reconciliation failure entry was logged.

Private activation evidence is retained under
`/opt/snowshot-v2/bluegreen/staging/prod-20261103-payments-on`; local manifest and
public response evidence are under
`.secrets/deployment/prod-20261103-payments-on`.

## Recovery

The immediate previous API release `prod-20261103-8954efb-b` remains retained
with the same application image and policy 17, but purchase creation disabled.
Use the blue/green controller for rollback; retain the new database schema,
account encryption key and financial evidence.

The older API release `policy-17-c13a440` and its matching workers also remain
retained. That binary does not implement the new account features. After enabling
financial traffic, review account-funded operation compatibility before using
it for recovery. The historical revision-18 recovery configuration is documented
in the [22 October record](production-release-20261022.md).
