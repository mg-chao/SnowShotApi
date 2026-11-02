# Account API v2

All routes below are relative to `/api/v2`. Success is `{ "code": 0, "message": "Request success", "data": ... }`; errors use the existing `application/problem+json` shape with stable `code`. JSON field names are snake_case. Times are ISO-8601 UTC. Monetary nano-yuan values are **decimal strings**, including signed transaction amounts. Package prices are integer fen. Never trust client-supplied prices or payment success.

Authorization is `Authorization: Bearer <access_token>`. Registration private operations additionally require `X-Registration-Proof: <registration_proof>`; the proof belongs only to the initiating desktop installation and is never encoded in a Mini Program scene. Invalid supplied authentication returns 401; it never falls back to anonymous usage. A valid unlinked Mini session may use the existing anonymous free allowance.

## Authentication

- `POST /auth/login`: `{username,password}`; active accounts only.
- `POST /auth/wechat`: `{code}` from `wx.login`; returns a Mini session, including when no Snow Shot account is linked.
- `POST /auth/refresh`: `{refresh_token}`; rotates the session tokens.
- `POST /auth/logout`: `{refresh_token}`; revokes that session; idempotent success `{logged_out:true}`.

Login, WeChat login, refresh and registration claim return:

```json
{"access_token":"opaque","refresh_token":"opaque","expires_at":"2026-11-02T00:15:00Z","refresh_expires_at":"2026-12-02T00:00:00Z","account_id":null}
```

`account_id` is a UUID string when linked, otherwise null. Sessions last 15 minutes with a 30-day refresh lifetime. Do not store passwords. Username: ASCII letters/digits/underscore/dot/hyphen, 3–32 characters, case-insensitive uniqueness, preserved display case. Password: 8–16 characters, no trimming.

## Desktop registration

- `GET /registration/captcha`: `{captcha_id,image_data_url,expires_at}`. Refreshing obtains a new four-character, single-use image challenge.
- `POST /registration/requests`: `{username,password,password_confirmation,captcha_id,captcha_answer}`. Validates and reserves the username, generates a ten-minute purchase code. Returns private purchase request plus `registration_proof`.
- `GET /registration/requests/{request_id}`: proof required; private status.
- `POST /registration/requests/{request_id}/refresh`: proof required; queries the platform for any issued order, returning private status. It cannot mark a payment successful.
- `POST /registration/requests/{request_id}/claim`: proof required; fulfilled registration only; returns session tokens. Retries recover the same bootstrap session, renewing expired tokens when necessary, for 30 days after fulfillment. An explicitly revoked bootstrap session cannot be resurrected by the proof.
- `POST /registration/requests/{request_id}/link`: Mini bearer; body `{}`. Opening a scanned registration page automatically calls this before enabling checkout. An active temporary WeChat account adopts the reserved desktop username/password, preserving its account ID, wallet, membership and history. The request must be unexpired, awaiting payment, and have no issued order. It becomes `fulfilled` with `completion_kind:"account_link"` and `order_id:null`; no payment or recharge is created. Repeats by the same WeChat identity recover the completed result. Unlinked users receive the unchanged public request and can proceed to payment. Already regular accounts cannot adopt another registration. Desktop tokens remain available only through the private proof-based claim endpoint.

Private purchase request:

```json
{"request_id":"uuid","username":"Example","purpose":"registration","state":"awaiting_payment","generated_at":"2026-11-02T00:00:00Z","expires_at":"2026-11-02T00:10:00Z","server_time":"2026-11-02T00:00:00Z","mini_program_code_data_url":"data:image/png;base64,...","order_id":null,"payments_available":true}
```

States: `awaiting_payment`, `payment_pending`, `fulfilled`, `expired`, `failed`, `reconciliation_required`. Only `awaiting_payment` expires after ten minutes. `payment_pending` keeps its username/identity reservation and polling continues beyond expiry. `order_id` is an opaque merchant order string or null. Expired requests require a fresh form/CAPTCHA/request; original codes cannot create orders. Returned code image may be null in status responses; retain the image from creation.

Private and public purchase responses also include `completion_kind`: null before completion, `payment` for verified payment, or `account_link` for desktop adoption of an existing temporary account. Existing fulfilled paid requests are reported as `payment` without requiring a data backfill.

## Packages and purchases

- `GET /purchases/packages`: `{payments_available,packages:[...]}`. Each package is `{id,price_fen,credits_nano_yuan}`. IDs: `cny-9-9`, `cny-29-9`, `cny-88-8`, `cny-188`, `cny-999`; prices: 990,2990,8880,18800,99900 fen. Credits equal the payer's verified actual payment; membership and credits never expire.
- `POST /purchases/requests`: linked active account bearer; body `{}`; creates a top-up code and returns the private request shape with `purpose:"top_up"`. No registration proof is needed; subsequent private information is scoped to the authenticated account.
- `POST /purchases/mini-program/requests`: Mini bearer; body `{}`. Creates or recovers a direct purchase request without generating a QR code. Linked accounts receive `purpose:"top_up"`; unlinked users receive `purpose:"mini_registration"` and `username:null` until verified fulfillment. The request is restricted to its initiating WeChat identity. Repeated calls recover an unresolved direct request, including uncertain orders beyond the original expiry. A completed or definitively closed purchase permits a fresh request and merchant order.
- `POST /purchases/requests/{request_id}/refresh`: linked account bearer belonging to that request; refreshes its platform result and returns the private request shape. This supplies the desktop top-up polling/manual-refresh operation.
- `GET /purchases/requests/{request_id}`: public, unguessable scene ID; returns `{request_id,username,purpose,state,expires_at,server_time,payments_available,order_id}`. Optional valid bearer adds `order_id` for the owning account or payer; otherwise null. It never returns passwords, proof or session information.
- `POST /purchases/orders`: Mini bearer, `{request_id,package_id,checkout_attempt_id?}`. Checks request validity and both permanent/provisional identity links before issuing payment parameters. Returns `{order_id,request_id,state,payment_parameters}`. `state` is `payment_pending`. `payment_parameters` is `{mode,sign_data,pay_sig,signature}`; invoke `wx.requestVirtualPayment({mode,signData:sign_data,paySig:pay_sig,signature})` only after the user confirms the selected package. Server mode is `short_series_goods`.
- `POST /purchases/orders/{order_id}/refresh`: Mini bearer owning the order; platform query. Returns `{order_id,request_id,state,refund_status}`. Order states: `payment_pending`, `paid`, `failed`, `closed`, `reconciliation_required`. Refund status: null, `processing`, `completed`, `failed`.

The scene contains only the request UUID without hyphens (32 characters). Decode it as a UUID and serialize it with hyphens in JSON; preserve it and package selection through sign-in. Never start checkout automatically after sign-in. A canceled client payment still requires a server query. Package changes cannot replace an uncertain order. A separate new purchase always receives a new merchant order ID.

Recoverable checkout clients must durably store a cryptographically random, nonempty UUID `checkout_attempt_id` and its request/package before the first order POST. Retrying that exact attempt recovers the same order and its immutable `sign_data`/`pay_sig`; only `signature` is refreshed for the current WeChat session key. An attempt cannot be reused with another request/package (`checkout_attempt_conflict`, 409) or payer (`wechat_link_conflict`, 409). Once any platform result is observed, or the order is fulfilled, terminal or requires reconciliation, parameters are null and recovery is query-only. Missing platform results do not close orders or release identity claims.

Before invoking the payment SDK, clients must synchronously persist and verify a durable invocation marker keyed by merchant order ID. Storage failure aborts SDK invocation. A marked order must never invoke the SDK again, including after restart, login or another HTTP response; it can only query its backend result. A retained, unmarked attempt may be resumed only by explicit user action. Never reset an invocation marker after cancellation, timeout or a missing query result. Retire the active request journal only after definitive backend fulfillment or closure; keep per-order invocation markers. Clients without durable invocation tracking must omit `checkout_attempt_id`: separate repeated calls retain the legacy `payment_parameters:null` behavior. The API also recovers its own database commit acknowledgement without losing the initial parameters.

A crash between persisting the invocation marker and entering the SDK is inherently uncertain. It remains query-only and may require platform/operator reconciliation. Neither a two-step HTTP launch handshake nor a local timeout proves that payment cannot still occur. The official SDK contract makes merchant IDs single-use and explicitly does not guarantee uniqueness in extreme cases, so SDK replay is not a safe recovery strategy.

Payments default disabled. Package/account reads remain available and return `payments_available:false`; payment-dependent creation endpoints return 503 `payments_unavailable`. No fake success or locally generated payment code is supplied.

Development with `Accounts:Wechat:AllowPaymentTesting=true` prepends the test package `cny-0-1`, priced at 10 fen (CNY 0.10), with `credits_nano_yuan:"100000000"`. Its configured WeChat product ID is `p0`. The same catalog controls package reads, order creation and payment signing. The testing flag is rejected outside Development; normal environments retain only the five standard packages, even if a test-product mapping is present.

## Account and transactions

- `GET /account`: linked account or valid Mini session. Returns:

```json
{"linked":true,"account_id":"uuid","username":"Example","membership_status":"active","available_credit_nano_yuan":"0","held_credit_nano_yuan":"0","outstanding_balance_nano_yuan":"0","payments_available":true,"cumulative_recharge_nano_yuan":"188000000000","membership_tier":"cny-188","free_table_extraction":true,"free_latex_extraction":true}
```

`cumulative_recharge_nano_yuan` is verified fulfilled actual payments minus completed, applied refunds, expressed as a decimal string. Spending credits, holding credits and repaying debt do not reduce it. Successful refunds reduce it exactly once. Pending, failed and unfulfilled payments do not count; processing refunds do not reduce it. Development test payments count at their actual amount.

`membership_tier` is the highest cumulative threshold reached: `cny-9-9` (990 fen), `cny-29-9` (2990 fen), `cny-88-8` (8880 fen), `cny-188` (18800 fen), or `cny-999` (99900 fen). It is null below 990 fen. Multiple payments qualify just like a single payment. Refunds may downgrade the tier, while `membership_status` continues to indicate account activation. Membership has no time expiry.

At cumulative net recharge **at least CNY 188**, `free_table_extraction` and `free_latex_extraction` are true. With a linked account bearer, `/api/v1/table/extract` and `/api/v1/latex/extract` cost zero and consume neither wallet credit nor daily free allowance, including when wallet credit is zero or debt is outstanding. Existing admission limits and operator budgets still apply. Chat and translation keep their existing billing. Eligibility is captured atomically when reserving each operation; later refunds or payments affect only new operations, including when recovering a reservation after an uncertain commit. Clients should read the benefit flags rather than infer them from wallet balance or the latest purchase package.

An unlinked Mini session receives `linked:false`, null account/username/tier, `membership_status:"unlinked"`, zero balances and cumulative recharge, and both benefit flags false. Its Personal Center permits direct Mini Program purchases.

Account responses additionally include `is_temporary` (false for existing accounts and unlinked sessions). A first verified direct payment creates an active temporary account with username `wx_` followed by 24 random lowercase hexadecimal characters and `is_temporary:true`. No password is configured; password login returns `invalid_credentials`. WeChat sessions resolve the new account immediately without another login. Further Mini Program purchases credit the same account before and after desktop linking. Linking sets `is_temporary:false` and enables the chosen desktop credentials; temporary accounts and their benefits do not expire. Refunds do not remove activation or the WeChat link.

- `GET /account/transactions?limit=20&cursor=<opaque>`: linked bearer; `{items:[...],next_cursor:null}`. Default20/max100, newest first. Each item: `{id,occurred_at,type,amount_nano_yuan,status,order_id,description}`. Types `top_up`,`usage`,`refund`; amounts positive for top-ups and negative for usage/refunds. Status `pending`,`completed`,`failed`. Order ID is set for top-up/refund records. Refund processing is never rendered completed. Unlinked Mini sessions receive an empty list.

Outstanding balance blocks paid calls; the existing free allowance remains usable when it fully covers the request envelope, and qualifying membership recognition remains free independently of debt. New top-ups and unused wallet hold releases repay debt first. No self-service refund endpoint exists.

Stable errors include `invalid_credentials`, `account_inactive`, `invalid_session`, `username_unavailable`, `password_mismatch`, `invalid_captcha`, `request_expired`, `request_not_found`, `invalid_registration_proof`, `payment_pending`, `payment_not_received`, `wechat_link_conflict`, `package_unavailable`, `payments_unavailable`, `reconciliation_required`, `rate_limited`.

## Permanent account deletion

- `GET /account/deletion`: valid Mini Program bearer required. Returns `{can_delete,blockers,available_credit_nano_yuan,held_credit_nano_yuan,outstanding_balance_nano_yuan,deletion_version,membership_tier}`. `membership_tier` identifies the linked account's advanced-card tier from cumulative net recharge, computed using `MembershipRules.TierFor` under the same wallet lock: `cny-9-9`, `cny-29-9`, `cny-88-8`, `cny-188` or `cny-999`. It is null for unlinked users or cumulative net recharge below CNY 9.90. Clients may display only this card level while keeping exact balance fields for settlement checks. Money is exact decimal nano-yuan text. `deletion_version` is an opaque, authenticated snapshot of the account's current balance, membership funding and settlement state, scoped to this session. Do not calculate or edit it in clients.
- `POST /account/deletion`: valid Mini Program bearer and `{confirmation:"DELETE",wechat_code:"fresh wx.login code",acknowledge_forfeiture:true,deletion_version:"value from GET"}` required. The fresh WeChat identity must match the signed-in identity. Success is `{deleted:true}`. Discard all local account/session state after success. A new WeChat login is allowed but cannot recover the deleted account, membership, balance or credentials.

Deletion explicitly and permanently forfeits any available balance and membership rights; a positive balance does not prevent deletion. Clients must show the current exact balance and consequences, start with consent unchecked, and obtain a separate confirmation before requesting a fresh WeChat code and submitting. `deletion_state_changed` (409) requires rereading eligibility and obtaining consent again; never automatically replay confirmation after a state change. Missing explicit consent returns `invalid_confirmation` (400); a different current WeChat identity returns `wechat_identity_mismatch` (403).

Unsettled refunds (including received but not yet applied platform refund messages) and outstanding debt block deletion with `pending_refund` / `outstanding_balance`. For technical settlement safety, issued uncertain payments or unconfirmed delivery and reserved/dispatched usage also temporarily block with `pending_payment` / `active_usage`. The final POST rechecks everything atomically, returning `account_deletion_blocked` (409) when necessary. Completed, applied refunds and definitively closed/failed orders do not block deletion. Unissued checkout requests are canceled atomically during deletion. An issued order is never discarded based only on code expiry or a client-side payment cancellation; wait for the platform's definitive settlement or seek manual support.

Under the same purchase/account lock and wallet row locks used by settlement, deletion clears username/password credentials, permanent/provisional WeChat login links, registration proofs and bootstrap credentials, and revokes every existing access/refresh session, clearing their WeChat/session-key fields. Unlinked Mini users may also erase their login sessions and cancel unissued direct requests. A permanent account deletion timestamp and consent version, forfeited amount and financial adjustment remain as audit evidence; deleted accounts are inactive and cannot be reactivated by delayed payment fulfillment, old login credentials, registration claims or refresh tokens. Concurrent order creation, linking, authentication and inference reservations recheck state under the corresponding locks.

Transaction/order/refund and accounting evidence remains for financial verification, including the payment platform's payer identifier where needed to verify later refunds; it is not a retained login link. Ordinary identity/profile credentials and reusable payment signatures are removed. Later verified refunds may update financial evidence but never restore membership, login links or a deleted wallet's rights. This operation is not a promise that every legal financial record or backup is physically erased immediately. Deploy the `AddAccountDeletion` database migration before enabling these endpoints. No migration or real account deletion is performed by local compilation.

Legacy credential copies: temporary-account adoption now erases the unused registration draft's credentials immediately. Deletion and password-hash upgrades also clear older inactive, unreferenced drafts only when the same normalized username and password hash establish an exact relationship. A historical draft whose adopted account had already changed its password hash before these safeguards cannot be safely attributed from its username alone; deployments with such pre-existing data require a separate, evidence-based privacy audit. Do not broaden automatic deletion to unrelated registrations merely sharing a username.
