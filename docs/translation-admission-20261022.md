# Screenshot translation admission update

Prepared and deployed on 22 October 2026. The
[production release record](production-release-20261022.md) records activation,
verification, and the retained higher-revision recovery configuration.

Production inspection found HTTP 429 responses concentrated around the existing
60 requests/minute per-IP threshold. Screenshot translation sends one chat
request for each distinct text box. Multiple windows, successive screenshots,
and users sharing a public IP can exhaust that threshold during normal work.
After the policy-15 release, provider attempts contained no upstream HTTP 429.
The observed natural-minute peaks for the shared text route were 157 attempts
and 45,595 recorded input/output tokens.

## Candidate policy

| AI Model admission setting | Previous | Candidate |
| --- | ---: | ---: |
| Requests per minute per principal | 60 | 600 |
| Concurrent requests per principal | 4 | 8 |
| Queued requests per principal | 4 | 8 |
| Global concurrent requests | 64 | 64 |
| Global queue length | 128 | 128 |

Principals still derive from the client public IP. The provider access pool,
per-operation maximum, daily user allowance, and operator budgets remain in
force. Dedicated batch translation, table, and formula admission are unchanged.

The candidate uses policy revision **17**, skipping the retained revision-16
recovery configuration. Its default configuration fingerprint is:

```text
3c8aeb51e2771184c1ed6f7d02de8882bb9da2036b66ddfa69f9b0cc14b1d4c5
```

Rate and daily-allowance errors retain their existing machine-readable codes,
HTTP status, and retry metadata, but have distinct English and Chinese details.
Clients must not treat `allowance_exhausted` as a short-term admission failure.

## Coordinated client behavior

The Snow Shot client shares eight built-in chat slots across translation and
image consumers and starts requests at least 100 ms apart. It shares a server
cooldown across those consumers when translation receives HTTP 429 with
`rate_limited` or `queue_full` and a usable `Retry-After` or
`retry_after_seconds` value.

Only rejected translations that have not delivered content are automatically
replayed. They retain the input, use a fresh request ID, and allow at most three
attempts within a 120-second request deadline. Cancellation removes pending
work, including cooldown retries. Daily allowance exhaustion, ambiguous errors,
custom providers, and partial translations do not use this replay mechanism.
After a terminal 429, a translation job removes its unsent work and retains
completed and in-flight results for an explicit retry.

Original-image translation now includes the underlying error in its failure
summary. Diagnostic records contain error codes, attempt counts, and retry
delays, without source or translated text.

## Validation and release

Focused coverage includes the 600-request boundary and independent principals,
eight active requests with a queued ninth, localized HTTP 429 details and retry
metadata, shared client capacity/cooldowns, cancellation, bounded retries,
daily-allowance handling, partial-output preservation, and overlay error details.
The old 60-RPM configuration fails the new screenshot-rate regression; the
candidate passes. The three client translation catalogs are complete.

The default configuration passes the network-free `--describe-deployment`
command. Production overrides must be frozen and validated separately. This is
a fingerprint-changing policy update: follow the
[policy-changing maintenance procedure](bluegreen-deployment.md#policy-changing-maintenance),
drain the old API before activating revision 17, and prepare a compatible
revision-18 recovery configuration. Do not restart a lower-revision policy
after activation. No database schema migration is needed.

`Prepare-AiModelPolicy.py` defaults to the revision-17 overlay and its revision.
`Verify-DeploymentConfiguration.ps1` checks candidate admission against that
overlay, including the effective per-principal queue limit. Its separate retained
policy check still uses the historical revision-15 reference. To prepare a
revision-18 recovery with the candidate's admission profile, pass `--revision 18`
to preparation and `-ExpectedPolicyRevision 18` when verifying that settings file.
To retain the older admission profile instead, explicitly select the revision-15
overlay with `--overlay` and `-PolicyReferencePath`; its recovery revision must
still exceed the activated policy. These checks validate configuration, not
whether a retained lower revision is safe to activate.

After deployment, check policy convergence, SSE completion, HTTP 429 frequency,
queue delay, provider RPM/TPM, and accounting settlement. The client changes
require a separate desktop release.

The provider's documented Beijing `qwen3.7-flash` limits are 30,000 RPM and
5,000,000 TPM. These are account-wide model limits, not per-user entitlements;
account/workspace settings and other services using the account must be checked
before release. Alibaba Cloud can also enforce second-level and burst limits.
See the [model limits](https://help.aliyun.com/zh/model-studio/qwen3-7-flash)
and [rate-limit rules](https://help.aliyun.com/zh/model-studio/rate-limit).
