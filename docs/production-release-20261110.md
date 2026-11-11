# Production release - 10 November 2026

Production serves commit `329c216e38fa3bdc89c072b92fad740ff3a591f9` as
`prod-20261110-329c216` on loopback API port **5000**. This release adds safe
cancellation of unissued top-up requests and preserves observed provider token
usage when a chat stream fails, is cancelled, or is disposed.

The API image is:

```text
sha256:b9a4b7505fec90632525dd6e55579187a2216cb0131f653d760cc7b5824825e6
```

The image was built on the workstation from the clean, current `main` commit
using the pinned Dockerfile. Its revision label and the transferred archive's
SHA-256 were verified on the API host before staging. The installed deployment
agent matches the repository source.

The candidate inherits the frozen production configuration; the only changed
configuration value is `ASPNETCORE_URLS`. Policy revision **17** and fingerprint
`3c8aeb51e2771184c1ed6f7d02de8882bb9da2036b66ddfa69f9b0cc14b1d4c5`
are retained. Production purchasing remains enabled with five standard packages.
The existing table and LaTeX workers remain assigned to ports **18080** and
**18081**. All fourteen database migrations already match the candidate; no
production migration was required.

## Verification

- 143 focused .NET provider, public-contract, account-lifecycle, host and
  deployment-description tests passed without skips.
- Five top-up cancellation integration cases passed against a separate temporary
  PostgreSQL instance, covering idempotency, authorization, issued-payment
  preservation and checkout concurrency. The temporary instance was removed.
- Formatting verification for changed C# files and the OpenAPI snapshot check
  passed. The unrelated full test suite was not run.
- Both workers passed local and reverse-tunnel mTLS and recognition probes.
- Candidate health, policy convergence, model catalogs, package catalog and
  signed WeChat callback checks passed. The new cancellation endpoint returns
  `401 invalid_session` without an authenticated session.
- After switching, workstation checks confirmed public readiness identifies the
  new release. Public table and LaTeX recognition matched the reviewed fixtures,
  and a real chat SSE request completed with `[DONE]`. The package catalog still
  exposes five packages with `payments_available:true`. No real payment was made.
- The five-minute readiness, release identity, policy and memory observation
  passed. Cleanup stopped the previous API after nginx drained, retaining its
  recovery artifacts and the shared recognition workers.
- The final check at **23:48 China Standard Time** confirmed all health
  components ready, no pending deployment operation, zero API errors or restarts,
  no OOM kill and **686 MiB** available memory. PostgreSQL, all three Redis
  containers and telemetry retained their original identities and start times.

The staging control connection disconnected after candidate startup. Persisted
state and private health confirmed the candidate was running while ingress still
served the previous release. The identified idle deployment-agent process was
terminated to release its orphaned lock. A new controller session verified state
before switching; application processes were not restarted during recovery.

Private manifests, image archive and verification evidence are retained under
`/opt/snowshot-v2/bluegreen/staging/prod-20261110-329c216`, with local evidence
under `.secrets/deployment/prod-20261110-329c216`.

## Recovery

The previous release, `prod-20261103-payments-on`, is retained with its image,
configuration, enabled purchase gates and policy 17. Use the blue/green
controller's `rollback` operation to restore it. Database schema and financial
evidence must be retained.
