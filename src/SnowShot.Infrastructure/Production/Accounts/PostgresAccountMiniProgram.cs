using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;
using static SnowShot.Infrastructure.Accounts.AccountErrors;
using static SnowShot.Infrastructure.Accounts.PostgresAccountStore;

namespace SnowShot.Infrastructure.Accounts;

internal sealed partial class PostgresAccountPurchases
{
    public async Task<AccountPurchaseRequest> MiniProgramPurchaseAsync(AccountSessionIdentity identity, CancellationToken token)
    {
        requests.RequirePayments();
        if (identity.OpenId is null) throw InvalidSession();
        await store.ThrottleAsync("mini-purchase:" + identity.OpenId, 10, token);
        var requestId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ChangeIdentityAsync(db, token);
            await RequireLiveSessionAsync(db, identity, now, token, requireWechat: true);
            await PostgresPurchaseRequests.ExpireRequestsAsync(db, now, token);
            // Resolve the permanent link again under the purchase lock: the bearer
            // may have been resolved immediately before a payment was fulfilled.
            var linkedId = await db.AccountWechatLinks.Where(x => x.OpenId == identity.OpenId)
                .Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(token);
            var retained = await db.AccountPurchaseRequests.SingleOrDefaultAsync(x => x.PayerOpenId == identity.OpenId &&
                (x.State == "awaiting_payment" || x.State == "payment_pending" || x.State == "reconciliation_required"), token);
            // Also recover an acknowledged commit if a concurrent platform event
            // fulfilled this very request before the execution strategy retried.
            retained ??= await db.AccountPurchaseRequests.SingleOrDefaultAsync(x => x.Id == requestId, token);
            if (retained is not null)
            {
                if (linkedId is not null && linkedId != retained.AccountId) throw LinkConflict();
                var retainedName = await db.Accounts.Where(x => x.Id == retained.AccountId).Select(x => x.Username).SingleAsync(token);
                return requests.View(retained, retainedName, now);
            }
            if (await db.AccountPayerClaims.AnyAsync(x => x.OpenId == identity.OpenId, token)) throw LinkConflict();
            AccountEntity account;
            if (linkedId is { } existingId)
                account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == existingId && x.Active, token) ?? throw InvalidSession();
            else
            {
                var pendingName = await PostgresPurchaseRequests.UnusedUsernameAsync(db, "pending_", token);
                account = new AccountEntity
                {
                    Id = accountId,
                    Username = pendingName,
                    NormalizedUsername = AccountRules.NormalizeUsername(pendingName),
                    PasswordHash = string.Empty,
                    CreatedAt = now
                };
                db.Accounts.Add(account);
                db.AccountWallets.Add(new() { AccountId = accountId });
                db.AccountUsernameClaims.Add(new() { AccountId = accountId, NormalizedUsername = account.NormalizedUsername, RequestId = requestId });
                db.AccountPayerClaims.Add(new() { AccountId = accountId, OpenId = identity.OpenId, RequestId = requestId });
            }
            var request = new AccountPurchaseRequestEntity
            {
                Id = requestId,
                AccountId = account.Id,
                Purpose = linkedId is null ? "mini_registration" : "top_up",
                PayerOpenId = identity.OpenId,
                State = "awaiting_payment",
                GeneratedAt = now,
                ExpiresAt = now.AddMinutes(10)
            };
            db.AccountPurchaseRequests.Add(request);
            return requests.View(request, account.Username, now);
        }, token);
    }

    public async Task<PublicPurchaseRequest> LinkRegistrationAsync(AccountSessionIdentity identity, Guid requestId, CancellationToken token)
    {
        if (identity.OpenId is null) throw InvalidSession();
        await store.ThrottleAsync("mini-link:" + identity.OpenId, 30, token);
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ChangeIdentityAsync(db, token);
            await RequireLiveSessionAsync(db, identity, now, token, requireWechat: true);
            await PostgresPurchaseRequests.ExpireRequestsAsync(db, now, token);
            var request = await db.AccountPurchaseRequests.SingleOrDefaultAsync(x => x.Id == requestId && x.Purpose == "registration", token) ?? throw NotFound();
            if (request.State == "expired") throw new AccountException("request_expired", "This registration code has expired.", 410);
            var target = await db.Accounts.SingleAsync(x => x.Id == request.AccountId, token);
            if (target.DeletedAt is not null) throw NotFound();
            var account = await (from link in db.AccountWechatLinks
                                 join value in db.Accounts on link.AccountId equals value.Id
                                 where link.OpenId == identity.OpenId && value.Active
                                 select value).SingleOrDefaultAsync(token);
            if (request.State == "fulfilled")
            {
                if (account?.Id != request.AccountId) throw LinkConflict();
                return requests.PublicView(request, account.Username, now, true);
            }
            if (account is null)
            {
                var ownsOrder = await db.AccountPaymentOrders.AnyAsync(x => x.RequestId == requestId && x.OpenId == identity.OpenId, token);
                return requests.PublicView(request, target.Username, now, ownsOrder);
            }
            if (!account.IsTemporary) throw LinkConflict();
            if (request.State != "awaiting_payment" || request.OrderId is not null)
                throw new AccountException("payment_pending", "An issued payment order must be resolved before linking.", 409);
            if (target.Active || await db.AccountPayerClaims.AnyAsync(x => x.AccountId == target.Id || x.OpenId == identity.OpenId, token)) throw LinkConflict();
            var claim = await db.AccountUsernameClaims.SingleOrDefaultAsync(x => x.RequestId == requestId && x.AccountId == target.Id, token);
            if (claim is null) throw new AccountException("username_unavailable", "The reserved username is no longer available.", 409);
            // Remove the generated-name claim before transferring the reserved
            // desktop name; AccountId has a unique index on username claims.
            await db.AccountUsernameClaims.Where(x => x.AccountId == account.Id).ExecuteDeleteAsync(token);
            account.Username = target.Username;
            account.NormalizedUsername = target.NormalizedUsername;
            account.PasswordHash = target.PasswordHash;
            account.IsTemporary = false;
            target.Username = "merged_" + Guid.NewGuid().ToString("N")[..24];
            target.NormalizedUsername = target.Username.ToUpperInvariant();
            target.PasswordHash = string.Empty;
            claim.AccountId = account.Id;
            request.AccountId = account.Id;
            AccountPurchaseLifecycle.CompleteAccountLink(request, now);
            return requests.PublicView(request, account.Username, now, true);
        }, token);
    }
}
