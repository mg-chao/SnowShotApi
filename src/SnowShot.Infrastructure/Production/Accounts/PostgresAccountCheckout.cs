using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using static SnowShot.Infrastructure.Accounts.AccountErrors;
using static SnowShot.Infrastructure.Accounts.AccountSerialization;
using static SnowShot.Infrastructure.Accounts.PostgresAccountStore;

namespace SnowShot.Infrastructure.Accounts;

internal sealed partial class PostgresAccountPurchases
{
    public async Task<AccountPurchaseOrder> CreateOrderAsync(AccountSessionIdentity identity, Guid requestId, string packageId,
        CancellationToken token, Guid? checkoutAttemptId = null)
    {
        // An omitted package can only look up an order for a durable attempt.
        var queryOnly = packageId.Length == 0 && checkoutAttemptId.HasValue;
        if (!queryOnly) requests.RequirePayments();
        if (identity.OpenId is null) throw new AccountException("invalid_session", "Sign in to WeChat before purchasing.", 401);
        if (checkoutAttemptId == Guid.Empty)
            throw new AccountException("invalid_checkout_attempt", "The checkout attempt identifier must be a nonempty UUID.");
        await store.ThrottleAsync("order:" + identity.OpenId, 20, token);
        var package = MembershipCatalog.For(options.AllowPaymentTesting).SingleOrDefault(x => x.Id == packageId);
        if (!checkoutAttemptId.HasValue && package.Id is null)
            throw new AccountException("package_unavailable", "This membership package is unavailable.");
        var orderId = Guid.NewGuid().ToString("N");
        var result = await store.ExecuteIdentityChangeAsync<AccountPurchaseOrder?>(async (db, now) =>
        {
            await PostgresPurchaseRequests.ExpireRequestsAsync(db, token);
            await RequireLiveSessionAsync(db, identity, now, token, requireWechat: true);
            var request = await db.AccountPurchaseRequests.SingleOrDefaultAsync(x => x.Id == requestId, token) ?? throw NotFound();
            if (await db.Accounts.AnyAsync(x => x.Id == request.AccountId && x.DeletedAt != null, token)) throw NotFound();
            if (request.State == "expired") return null;
            if (request.PayerOpenId is not null && request.PayerOpenId != identity.OpenId) throw LinkConflict();
            var session = await db.AccountSessions.SingleOrDefaultAsync(x => x.Id == identity.SessionId && x.OpenId == identity.OpenId && x.RevokedAt == null, token);
            if (session?.WechatSessionKeyEncrypted is null || session.AccessExpiresAt <= now) throw InvalidSession();
            if (checkoutAttemptId is { } attemptId)
            {
                var attempt = await db.AccountPaymentOrders.AsNoTracking().Where(x => x.CheckoutAttemptId == attemptId)
                    .Select(x => new { x.RequestId, x.OpenId, x.PackageId }).SingleOrDefaultAsync(token);
                if (attempt is not null)
                {
                    if (attempt.OpenId != identity.OpenId) throw LinkConflict();
                    if (attempt.RequestId != requestId || (!queryOnly && attempt.PackageId != packageId))
                        throw new AccountException("checkout_attempt_conflict", "This checkout attempt belongs to a different purchase.", 409);
                }
            }
            if (request.OrderId is not null)
            {
                var previous = await db.AccountPaymentOrders.SingleAsync(x => x.Id == request.OrderId, token);
                if (previous.OpenId != identity.OpenId) throw LinkConflict();
                if (!queryOnly && previous.PackageId != packageId) throw new AccountException("payment_pending", "The existing order must reach a final result before changing packages.", 409);
                // The attempt ID is an HTTP idempotency key, not permission to
                // invoke WeChat again. Participating clients durably record SDK
                // invocation before launch, and only replay an uninvoked attempt.
                // The local order ID also recovers this invocation's lost database
                // commit acknowledgement, including requests from legacy clients.
                var recovering = previous.Id == orderId ||
                    (checkoutAttemptId.HasValue && previous.CheckoutAttemptId == checkoutAttemptId);
                var canAuthorize = recovering && !queryOnly && package.Id is not null &&
                    AccountPurchaseLifecycle.CanRecoverCheckout(previous, request);
                var authorization = canAuthorize
                    ? platform.RefreshPaymentAuthorization(
                        JsonSerializer.Deserialize<VirtualPaymentParameters>(secrets.Unprotect(previous.PaymentParametersEncrypted), Json)
                            ?? throw new AccountException("reconciliation_required", "The original checkout authorization is unavailable.", 409),
                        secrets.Unprotect(session.WechatSessionKeyEncrypted))
                    : null;
                return new AccountPurchaseOrder(previous.Id, requestId,
                    await AccountPurchaseLifecycle.ReportedOrderStateAsync(db, previous, token), authorization);
            }
            if (request.State != "awaiting_payment") throw new AccountException("payment_pending", "This purchase request cannot create another order.", 409);
            if (queryOnly || package.Id is null)
                throw new AccountException("checkout_not_issued", "No order was issued. Select an available package.");
            var existingLinks = await db.AccountWechatLinks.Where(x => x.OpenId == identity.OpenId || x.AccountId == request.AccountId).ToListAsync(token);
            if (existingLinks.Any(x => x.OpenId != identity.OpenId || x.AccountId != request.AccountId)) throw LinkConflict();
            var existingLink = existingLinks.SingleOrDefault();
            var payerClaims = await db.AccountPayerClaims.Where(x => x.OpenId == identity.OpenId || x.AccountId == request.AccountId).ToListAsync(token);
            if (payerClaims.Any(x => x.OpenId != identity.OpenId || x.AccountId != request.AccountId || x.RequestId != requestId)) throw LinkConflict();
            if (request.Purpose == "top_up" && existingLink is null) throw LinkConflict();
            if ((request.Purpose == "registration" || request.Purpose == "mini_registration") && payerClaims.Count == 0)
                db.AccountPayerClaims.Add(new() { AccountId = request.AccountId, OpenId = identity.OpenId, RequestId = requestId });
            var parameters = platform.Payment(orderId, identity.OpenId, secrets.Unprotect(session.WechatSessionKeyEncrypted), packageId, package.Fen);
            db.AccountPaymentOrders.Add(new()
            {
                Id = orderId,
                CheckoutAttemptId = checkoutAttemptId,
                RequestId = requestId,
                AccountId = request.AccountId,
                OpenId = identity.OpenId,
                PackageId = packageId,
                PriceFen = package.Fen,
                State = "payment_pending",
                PaymentParametersEncrypted = secrets.Protect(JsonSerializer.Serialize(parameters, Json)),
                CreatedAt = now
            });
            db.AccountFinancialEntries.Add(new()
            {
                Id = "topup:" + orderId,
                AccountId = request.AccountId,
                Type = "top_up",
                Status = "pending",
                AmountNanoYuan = checked(package.Fen * 10_000_000L),
                OrderId = orderId,
                OccurredAt = now,
                Description = "Membership purchase"
            });
            AccountPurchaseLifecycle.IssueCheckout(request, orderId);
            return new AccountPurchaseOrder(orderId, requestId, "payment_pending", parameters);
        }, token);
        return result ?? throw new AccountException("request_expired", "This purchase code has expired. Generate a new code in Snow Shot.", 410);
    }
}
