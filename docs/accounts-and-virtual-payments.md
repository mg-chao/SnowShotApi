# Accounts and virtual payments

The account API is documented in [the v2 contract](../contracts/account-v2.md).
Purchases use WeChat Mini Program **virtual payments**, including individual
developer and iOS support. This is separate from merchant WeChat Pay/APIv3.
Payments default disabled until platform prerequisites are confirmed.
Development supports explicitly enabled manual payment testing using the override
below. Automated local verification uses fake platform responses and an isolated
PostgreSQL database. The test ingress example forwards to a development API.

## Cumulative membership and recognition benefits

Membership uses cumulative net recharge: verified actual payments on fulfilled
orders minus completed refunds already applied to their wallets. The highest
reached threshold among CNY 9.9, 29.9, 88.8, 188 and 999 determines the tier,
independently of current credit or spending. Multiple payments accumulate;
development test payments count without introducing an additional tier.
Completed refunds reduce the cumulative amount and can downgrade membership.
Activation and permanent account links survive refunds.

At a cumulative net recharge of at least CNY 188, dedicated table and LaTeX
recognition cost zero, consuming neither wallet credit nor daily free allowance.
The benefit remains usable with zero balance or outstanding debt. Admission
limits, provider costs and operator budgets still apply. Eligibility is decided
under the wallet lock during reservation and retained in the operation snapshot;
later top-ups/refunds affect new operations only. Commit-acknowledgement recovery,
settlement retries and expired-operation reconciliation use the retained snapshot.
Free operations retain usage/cost events without creating a wallet debit.

The account response exposes `cumulative_recharge_nano_yuan`, `membership_tier`,
`free_table_extraction` and `free_latex_extraction`; see the v2 contract for exact
types. Payment fulfillment and both refund-application paths update cumulative
recharge in the same transaction as wallet effects, with the existing fulfillment
and refund idempotency guards.

Deploy `AddCumulativeMembership` in a maintenance window: stop all old API and
account reconciliation instances, apply migrations with the database migrator,
then start all instances on the new version. Do not mix old financial writers
with the new counter. The migration backfills from fulfilled orders' `PaidFen`
and completed, applied refunds, aggregating each independently so multiple
refunds never multiply payment amounts. It does not infer recharge from wallet
balances. Existing operation snapshots default to the previous pricing behavior.
After deployment, verify historical totals and test threshold crossings and
refund downgrades using the isolated fake-platform fixtures before resuming
normal traffic. Retain the existing financial evidence on rollback.

## Configuration and release gate

The `Accounts:Wechat` settings support key-per-file mounted secrets using the
existing `SNOWSHOT_SECRETS_DIRECTORY` mechanism. Provision `AppId`, `AppSecret`,
`OfferId`, the environment-matched `AppKey`, message-push `MessageToken` and 43-character
`EncodingAesKey`. Keep them out of source, command output and Mini Program files.
Account bootstrap credentials, event evidence and WeChat session keys use the
dedicated 32-byte base64 secret `Accounts:Encryption:KeyBase64`. Payments cannot
be enabled without it. Retain it across replicas/restarts, independently of the
IP-privacy HMAC key. For rotation, retain all prior keys in
`Accounts:Encryption:PreviousKeysBase64` until retained evidence is re-encrypted;
new writes use the current key and reads authenticate against the key ring.
With payments disabled and no dedicated key, development/bootstrap sessions use
the current/previous identity HMAC keys for backward compatibility.
Password hashes use the framework's versioned PBKDF2 format with configurable
`Accounts:PasswordHashIterations` (default 210000, permitted 100000–2000000).
Unknown-user checks derive against a dummy hash using the same configured work
factor. Passwords and CAPTCHA answers are never retained in plaintext.

Publish all five virtual products and populate `ProductIds` for `cny-9-9`,
`cny-29-9`, `cny-88-8`, `cny-188`, and `cny-999`. Their published product prices
must be 990, 2990, 8880, 18800 and 99900 fen respectively. `PurchasePage` defaults
to `pages/purchase/purchase`; the 32-character request scene contains no proof,
password or session token.

Mini Program codes default to `MiniProgramCodeVersion=release` and
`MiniProgramCodeCheckPath=true`. `appsettings.Development.json` selects
`MiniProgramCodeVersion=develop` and `MiniProgramCodeCheckPath=false` for local
debugging. Development may also explicitly set
`Accounts:Wechat:MiniProgramCodeVersion=develop` (or `trial`) and
`Accounts:Wechat:MiniProgramCodeCheckPath=false` to generate a code before the
purchase page is published. With path checks enabled, WeChat can return
`41030 invalid page` even for a development/trial code if the page is not in the
published Mini Program. Code generation then returns `503 payments_unavailable`.
The version must be exactly `release`, `trial` or `develop`; outside Development,
startup requires `release` with path checks enabled. These settings are separate
from the numeric virtual-payment `Environment` and do not change payment keys,
signing or settlement. Ensure the corresponding uploaded Mini Program version
contains `PurchasePage`, and use a WeChat account authorized to open that version.

The local debug launcher uses the Development settings above. To override the
version or path check through its key-per-file configuration, use the following
files and restart the launcher so it reloads its environment:

```text
.secrets/development/api/Accounts__Wechat__MiniProgramCodeVersion = develop
.secrets/development/api/Accounts__Wechat__MiniProgramCodeCheckPath = false
```

Configure encrypted message push to `https://<api-host>/api/v2/wechat/events`.
GET verifies the message-push token signature. POST requires the encrypted
message signature and validates decrypted recipient AppID before accepting a
durable inbox record. The platform receive acknowledgement is not a financial
completion. Background workers obtain fenced PostgreSQL inbox leases; failure
retains the event and retries later. Message push must subscribe to delivery and
refund events.

Confirm the Mini Program's individual-developer tools category and virtual
payment entitlement, published products, server request domain, Apple IAP
entitlement/short name, privacy review, and Android/iOS real-device behavior.
iOS checkout needs WeChat 8.0.68 or later. Only then set
`ReleasePrerequisitesConfirmed=true` and `PaymentsEnabled=true`. All
non-Development environments require `Environment=0` and the production AppKey.
This configuration gate does not replace the platform's entitlement checks.
For owner-requested manual testing, Development may instead use
`PaymentsEnabled=true` with `AllowPaymentTesting=true` while leaving
`ReleasePrerequisitesConfirmed=false`. Startup rejects `AllowPaymentTesting`
in every non-Development environment. Credentials, encryption, published-product
configuration remain required. Development with this explicit override may use
`Environment=1` and the sandbox AppKey; `Environment=0` uses the production AppKey
and real payments. Other environment values are rejected. Payment signing, order
queries and delivery notifications all use the configured environment. The
platform still applies its own eligibility requirements.

### Development payment configuration

Development manual testing (`AllowPaymentTesting=true`) additionally exposes
`cny-0-1` for CNY 0.10 (10 fen). Map it through
`Accounts:Wechat:ProductIds:cny-0-1`; its credit is `100000000` nano-yuan.
Package reads, order validation and payment signing use the same testing catalog.
Outside Development, startup rejects the testing flag and retains only the five
standard packages. Configure all six product mappings for manual testing.

Provision your own AppID, OfferID, platform keys and product IDs in the private
key-per-file directory. The checked-in AppID is empty. Keep deployment approval,
filing, payment acceptance evidence and actual host addresses in private records.
Select the sandbox AppKey with `Environment=1` for sandbox testing; production
uses its matching AppKey with `Environment=0`. Verify signed callbacks, encrypted
message reception, recipient AppID checks and duplicate suppression before
performing an authorized real payment.

Apply additive migrations through `SnowShot.DatabaseMigrator` before deploying
the new API. Existing anonymous operations keep nullable account funding and
their previous accounting behavior. Upgrade both API replicas before enabling
purchases. Test small genuine payments/refunds in an explicitly authorized
release session; local tests use an isolated PostgreSQL and fake platform.

## Payment, identity and refund invariants

The account API facade delegates session issuance and authentication to
`PostgresAccountSessions`, registration/linking/checkout to
`PostgresAccountPurchases`, financial settlement and recovery to
`PostgresAccountPayments`, and erasure to `PostgresAccountDeletion`.
`PostgresAccountStore` provides transaction retries, database time and shared
row operations. Each workflow still owns its lock boundary; fulfillment,
identity promotion, wallet funding and refunds remain atomic transactions.
`AccountPurchaseLifecycle` owns coupled order/request transitions and prevents
closing fulfilled payments, completing unfulfilled payments, or reissuing an
already-issued checkout. Existing state codes and database schemas are retained.

Platform delivery completion requires a valid JSON object with integer
`errcode: 0`. Empty, malformed, missing-code and failed responses remain
retryable; they cannot set `DeliveryConfirmed`. Delivery uses the same response
validation as other platform calls, so malformed responses become controlled
platform failures instead of aborting the reconciliation pass. Successful
retries confirm delivery without applying wallet credits again.

The server signs the exact product/price/quantity/order JSON. Query results must
match merchant order, environment and immutable signed order price. Actual
credits equal `paid_fee`, without subtracting platform commissions. Customer
callbacks and desktop refresh buttons cannot mark an order paid.

Registration expires ten minutes after code generation only while it has no
issued payment parameters. Issuance reserves both identity directions and keeps
the username until definitive platform closure. Status 0, network errors and
missing query results remain pending. Checkout preparation lives separately in
`PostgresAccountCheckout`; settlement remains in `PostgresAccountPayments`.
The optional `checkout_attempt_id` binds HTTP recovery to the original request,
package and payer, with database uniqueness across orders. Repeating an uninvoked
attempt recovers the original merchant order and signed document. A fresh WeChat
login updates only the user-session signature. A verified platform observation
fences further checkout authorization, including a still-pending platform order.
Legacy callers and different attempts receive null payment parameters.

The Mini Program persists the attempt before sending HTTP, and verifies a durable
per-order invocation marker before entering `wx.requestVirtualPayment`. Failure
to persist either record prevents payment. Lost HTTP responses and page reloads
before SDK invocation can resume the same attempt through an explicit action;
login, polling and page load never launch payment. Recorded SDK invocation is
query-only even after cancellation, timeout or restart. No local expiry or client
report closes an uncertain order or releases its identity claims.

The [official SDK contract](https://developers.weixin.qq.com/miniprogram/dev/api/payment/wx.requestVirtualPayment.md)
makes merchant IDs single-use and warns that uniqueness is not guaranteed in
extreme cases. There is an irreducible crash window between durably recording
invocation and entering the SDK. Such an attempt requires query/operator
reconciliation; automatically replaying it or issuing a replacement is unsafe.
The coordinated protocol recovers lost HTTP responses without claiming exactly
once execution across this platform boundary.

Apply `AddCheckoutRecoveryAndEventRouting` before deploying the updated API,
then deploy the Mini Program update. Existing orders retain null attempt IDs and
cannot be opted into replay by a new client. Keep old API writers stopped during
the upgrade so every replica records platform observations consistently. The
migration preserves order/payment evidence and adds only nullable attempt/routing
metadata and indexes; legacy refund routing is backfilled by the bounded worker.

Verified payments promote the identity claim, activate the account and add funds
in the same PostgreSQL transaction. Repeated queries/events cannot repeat this
effect. Historical orders always retain their original account ID. A late
payment conflicting with a reused username or identity enters
`reconciliation_required`; retain its received-money evidence and resolve it
through operator review/platform refund. Never credit the replacement account.

Refund callbacks use `MchOrderId`, `WxOrderId`, `MchRefundId`, `WxRefundId`,
`RefundFee` and `RetCode`. Match their payer, original payment and refund IDs and
amounts against backend platform queries. A successful notification plus a
confirmed customer-received refund-order result completes a refund. Status 5
means merchant debit/processing; only status 8 means customer receipt. A refund
request acknowledgement never completes a refund. Out-of-order completed
refunds are retained and netted atomically when their payment is fulfilled.

There is no self-service refund route and no server refund submission in v1.
Completed refunds deduct available credits and create outstanding debt for the
shortfall. Held inference funds remain held. New top-ups and unused hold
releases repay debt first. Activation and permanent WeChat links survive refunds.

## Retention, monitoring and recovery

### Fair delivery and refund retries

`AddAccountRetryScheduling` adds retry schedules, attempt counts and fenced leases
to payment orders and refunds. Existing eligible rows have no next-attempt time
and are immediately available. Eligibility remains on the financial record;
there is no second queue state that can disagree with fulfillment or refund state.
Apply this additive migration before upgrading API/reconciliation instances.
Stop old reconciliation writers during the upgrade: they do not honor the new
leases or retry schedule. No financial totals or payment evidence are changed.

Both automatic delivery confirmation and direct query recovery claim the same
delivery lease. Refund queries use the same queue mechanism. PostgreSQL selects
due work with `FOR UPDATE SKIP LOCKED`, records a two-minute lease and next-attempt
time before network I/O, and assigns an ownership token. A failed or still-pending
result is rescheduled with a 15-second exponential backoff capped at five minutes.
Missing/mismatched platform results also advance the schedule. Older unavailable
records therefore cannot occupy every retry batch. Expired leases recover crashed
workers; stale owners cannot clear a newer lease or apply a stale refund result.
Verified effects remain idempotent if callbacks arrive during a retry.

Monitor event 5504 for deferred delivery/refund work; it includes the record ID,
attempt and error category without platform bodies or credentials. Monitor the
oldest due record and expired leases alongside the existing financial alerts.

Refund inbox records retain the authenticated merchant order ID as indexed
routing metadata. Deletion checks use that index and do not decrypt unrelated
accounts' evidence. Legacy unresolved refund messages are indexed in batches of
at most 100 before reconciliation or deletion checks, outside identity and
wallet locks. Until all unresolved legacy messages have known routing, deletion
conservatively reports `pending_refund`; background reconciliation drains the
remaining batches. An unreadable legacy payload remains evidence and must be
recovered with the retained encryption key before deletion can proceed. Each
attempt records database time, so unreadable rows rotate behind evidence that
has not been attempted and cannot monopolize the bounded batches. Duplicate
callbacks also upgrade their retained routing metadata without repeating the
financial effect. Apply the additive routing migration before starting the new
API; stop old callback writers during the upgrade.
Monitor event 5505 for unreadable retained routing evidence; its event ID and
error category exclude ciphertext and credentials.

### Account concurrency and paid-chat authorization

The identity gate is shared by ordinary login, token refresh, account status,
top-up fulfillment and refund settlement. Mutable sessions, accounts, platform
identifiers and wallets are serialized at their own rows or advisory keys.
Registration, identity reservation/linking, claim expiration and deletion retain
exclusive identity coordination. Password derivation occurs before entering that
gate. Status polling only requests exclusive expiration work when its request is
actually due; it no longer scans and expires every pending purchase on each read.
Lock order is identity gate, account, financial evidence, then financial rows.
Inference retains its existing allowance/budget/wallet lock order.

Paid text chat uses an opt-in `BillingEnvelope:TextInput` profile for the configured
Qwen byte-level BPE routes. The conservative input bound is the normalized request's
serialized UTF-8 byte length plus configured request/message framing allowances
(64 tokens per request and 16 per message). This covers textual input and bounded
ChatML framing without reserving the entire model context for every small prompt.
The profile is based on [Qwen's tokenizer implementation](https://github.com/huggingface/transformers/blob/main/src/transformers/models/qwen2/tokenization_qwen2.py)
and [Qwen's chat format](https://github.com/QwenLM/Qwen3/blob/main/docs/source/getting_started/concepts.md).
Only enable this profile for routes whose tokenizer and template satisfy those
bounds. Other routes retain the full context envelope. Images, provider-injected
JSON schemas and translation options also retain the full context envelope;
their token expansion is not inferred from the JSON payload size.

The hold includes the enforced completion limit and output tolerance, using the
maximum charge across every reachable tariff tier even if rates decrease between
tiers. Actual usage still determines settlement, unused holds are released, and
the existing authorized-charge ceiling remains in force.

### Resolved sandbox checkout incident — 2026-11-02

A CNY 0.10 sandbox purchase succeeded in WeChat but remained `payment_pending`
locally and the Mini Program displayed `payments_unavailable`. The token request
used streaming `PostAsJsonAsync` content without `Content-Length`; WeChat returned
HTTP 412 before any JSON platform error. The same request with buffered UTF-8
JSON and a declared content length returned HTTP 200. All WeChat JSON POSTs now
send a known content length. Payment queries and delivery confirmations share
the exact-body `pay_sig` signing path; the former delivery implementation omitted
the signature and returned platform error 268490002. Sandbox delivery callbacks
also validate their environment against the retained signed order, allowing 0
or 1 and rejecting mismatches.

The original merchant order was recovered through the normal verified query and
fulfillment path: 10 fen credited once, the temporary account activated, and
delivery confirmed with WeChat. No replacement payment or manual wallet edit
was needed. HTTP/JSON failures now log the operation and status, and platform
errors log only their operation and numeric error code; credentials, signed
URLs and response bodies remain excluded. Focused regression tests cover request
lengths, delivery signatures, and production/sandbox callback environments.

Account financial entries, usage idempotency tombstones, purchase requests,
orders, identity links and platform evidence are retained independently of the
90-day operational usage cleanup. Expiring a request only removes its username
claim; it never deletes an order or financial record. CAPTCHA and throttle rows
are disposable and expire separately.
Delivery callbacks validate `Env`, payer, product, quantity, original price,
actual price, attach, and `WeChatPayInfo.MchOrderNo` against the immutable signed
order. XML and JSON nested fields are supported. The verified callback fulfills
the order transactionally before returning the documented XML/JSON `ErrCode:0`
response. Platform transaction IDs have a unique database index and fulfillment
is serialized transactionally. A differently serialized repeat notification
cannot issue credits again. Non-WeChat channels that omit `WeChatPayInfo` first
recover the platform transaction ID using `query_order`.

Confirmed local fulfillment also retains a delivery-confirmation outbox flag.
Query-based recovery calls the documented `notify_provide_goods` endpoint after
the credit transaction; failures retry without adding credits again. Normal
delivery callbacks already acknowledge platform delivery. Disabling new
purchases leaves retained payment/refund queries and delivery confirmation
enabled while credentials remain configured.

Monitor event 5501 (`Account payment reconciliation failed`), pending inbox
attempts/lease age, pending order age, `reconciliation_required` orders, duplicate
platform transaction IDs and outstanding balances. A temporary platform query
failure does not change a request to failed. Polling is cooled down centrally
for three seconds and automatic unresolved-order reconciliation uses five-minute
query intervals. Investigate long-pending orders with platform evidence before
releasing an identity claim. Do not edit wallet totals manually.

For a refund/payments incident, preserve the inbox, order and refund evidence;
re-query the original immutable merchant IDs. Completed effects are idempotent.
Rollback may disable new purchases while retaining callbacks and reconciliation.
Do not run downward financial migrations after receiving money.

## Mini Program integration

### Direct WeChat purchases and desktop account linking

Mini Program checkout uses `POST /api/v2/purchases/mini-program/requests` for both
unlinked WeChat users and existing accounts. It does not generate a QR image.
An initial request stages an inactive internal account and wallet to preserve
immutable order ownership, reserves the initiating WeChat identity, and hides
the internal username. Verified fulfillment alone generates a `wx_` username,
activates a passwordless temporary account, establishes the permanent WeChat
link, and applies the verified payment. Abandoned requests expire after ten
minutes and release both reservations only if no order has been issued.
Uncertain orders retain their identity and are recovered rather than replaced.

Opening a desktop registration code calls the authenticated registration `link`
endpoint before checkout. A paid temporary account adopts the reserved desktop
credentials in one purchase-locked transaction. Its original account ID, orders,
refunds, financial entries, wallet and membership remain in place. The unused
desktop registration placeholder stays inactive. A request with an issued order
cannot be converted. Regular-account links cannot be merged or replaced.
The completed request has `completion_kind:account_link` and no order; desktop
polling exchanges its existing private registration proof for a session as usual.
Linking itself has no financial effect and does not require new payments to be
enabled. Clients distinguish this completion from `completion_kind:payment`.

Deploy `AddTemporaryWechatAccounts` before the new clients: stop old API and
account reconciliation writers, apply the additive migration with the database
migrator, and upgrade all API instances/workers together. Do not mix financial
writers from before and after this change. Existing accounts default to regular;
existing paid requests infer their payment completion kind. Verify the new
first-purchase/link/repeat-purchase flow with fake-platform isolated database
fixtures, then release the Mini Program and desktop updates. Existing platform
payment gates remain authoritative. A rollback must preserve all account and
financial evidence; do not run downward migrations after accepting money.

Configure the Mini Program's own AppID and API origin in its private deployment
settings. Device development/trial builds need an HTTPS test origin; the
simulator can use localhost. Release builds require an explicit public API
origin. Add the relevant domain to the Mini Program request-domain allowlist.

The existing purchase page normalizes the 32-character scene to the UUID JSON
format, signs in with `wx.login`, and passes the signed strings through
`services/payment.ts` to `wx.requestVirtualPayment` without rewriting them.
It permits iOS WeChat 8.0.68 and newer, including exactly 8.0.68. After SDK success,
cancellation or failure, it queries the backend and waits for backend fulfillment.
Null payment parameters mean query-only recovery; checkout skips the SDK
and queries its result. Uninvoked attempts with a durable local journal expose
an explicit resume action using the same `checkout_attempt_id`. Pending orders retain their identity, lock package
selection, poll every four seconds, and pause polling while the page is hidden.
Login never starts checkout. This has mocked SDK coverage, not real-device
payment approval.

## Test ingress example

Copy `scripts/Run-ApiTestReverseTunnel.example.ps1` to the ignored
`scripts/Run-ApiTestReverseTunnel.ps1` and configure your SSH host and account.
Provision its private key under `.secrets/test-api-tunnel/id_ed25519` and verify
the server host key before starting the tunnel. Restrict the SSH account to
remote forwarding to the test listener.

Adapt `deployment/nginx/test-api.example.conf` for your own test domain and
certificate paths. The example terminates HTTPS and forwards to a loopback SSH
listener on port 15101; the tunnel connects it to the development API on port
5101. Configure the Mini Program callback as
`https://<test-api-host>/api/v2/wechat/events`. Keep live domain names, SSH
accounts, scheduled-task settings, certificate details and workstation paths in
ignored deployment files. The workstation, local API and tunnel must be running
for callbacks to work.

For local worker routing, copy `compose.local.example.yaml` to the ignored
`compose.local.yaml` and replace the example worker address. Run deployment
installers with explicit host parameters; communication key generation requires
both `-WorkerLanIp` and `-ApiHost`.

## Deployment acceptance

Before enabling production payments, record the following in private release
evidence:

- Platform entitlement, certification/filing, product publication and request-domain approval.
- Matching production AppID, OfferID, AppKey and message-push credentials.
- Signed callback verification, encrypted reception and idempotent delivery.
- Android/iOS real-device purchase and backend fulfillment.
- Order-query recovery, refund processing and platform bill reconciliation.

Keep `ReleasePrerequisitesConfirmed` and `PaymentsEnabled` disabled until the
required platform checks and real-payment evidence are complete. Use focused
API/database tests with fake platform responses before authorized device checks.

Primary platform references: [virtual payment](https://developers.weixin.qq.com/miniprogram/dev/platform-capabilities/business-capabilities/virtual-payment),
[individual developers](https://developers.weixin.qq.com/miniprogram/dev/platform-capabilities/business-capabilities/virtual-payment/person),
[query order](https://developers.weixin.qq.com/miniprogram/dev/server/API/VirtualPayment/api_query_order),
[message push](https://developers.weixin.qq.com/miniprogram/dev/framework/server-ability/message-push.html).
