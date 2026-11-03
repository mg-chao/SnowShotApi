using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;
using static SnowShot.Infrastructure.Accounts.AccountErrors;
using static SnowShot.Infrastructure.Accounts.PostgresAccountStore;

namespace SnowShot.Infrastructure.Accounts;

internal sealed partial class PostgresAccountPurchases(PostgresAccountStore store, IWechatAccountPlatform platform,
    AccountSecrets secrets, TimeProvider clock, WechatAccountOptions options, PasswordHasher<AccountEntity> passwords,
    PostgresPurchaseRequests requests, PostgresAccountPayments payments)
{
    public async Task<AccountPurchaseRequest> RegisterAsync(RegisterAccountCommand command, string clientKey, CancellationToken token)
    {
        requests.RequirePayments();
        await store.ThrottleAsync("register:" + clientKey, 10, token);
        if (!AccountRules.IsUsername(command.Username)) throw new AccountException("invalid_request", "Use 3–32 ASCII letters, digits, underscores, dots or hyphens for the username.");
        if (!AccountRules.IsPassword(command.Password)) throw new AccountException("invalid_request", "Use 8–16 characters for the password.");
        if (!string.Equals(command.Password, command.PasswordConfirmation, StringComparison.Ordinal)) throw new AccountException("password_mismatch", "The passwords do not match.");
        var validationReceipt = Guid.NewGuid();
        var valid = await store.ExecuteAsync(async (db, now) =>
        {
            var captcha = await db.AccountCaptchas.FromSqlInterpolated($"SELECT * FROM snowshot.account_captchas WHERE \"Id\"={command.CaptchaId} FOR UPDATE").SingleOrDefaultAsync(token);
            if (captcha is null || captcha.Consumed && captcha.ValidationReceipt != validationReceipt ||
                captcha.ExpiresAt <= now && captcha.ValidationReceipt != validationReceipt || captcha.ClientKey != secrets.Fingerprint(clientKey))
                throw new AccountException("invalid_captcha", "Refresh the CAPTCHA and try again.");
            captcha.Consumed = true;
            captcha.ValidationReceipt = validationReceipt;
            return CryptographicOperations.FixedTimeEquals(captcha.AnswerHash, PostgresAccountSessions.CaptchaHash(captcha.Id, command.CaptchaAnswer));
        }, token);
        if (!valid) throw new AccountException("invalid_captcha", "The CAPTCHA is incorrect. Refresh it and try again.");
        await using (var preflight = await store.OpenAsync(token))
        {
            var normalized = AccountRules.NormalizeUsername(command.Username);
            var now = clock.GetUtcNow();
            if (await (from claim in preflight.AccountUsernameClaims
                       join request in preflight.AccountPurchaseRequests on claim.RequestId equals request.Id
                       where claim.NormalizedUsername == normalized && (request.State != "awaiting_payment" || request.ExpiresAt > now)
                       select claim).AnyAsync(token))
                throw new AccountException("username_unavailable", "This username is already in use or temporarily reserved.", 409);
        }
        var requestId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var proof = AccountSecrets.Token();
        var code = await platform.CodeAsync(requestId, token);
        // PasswordHasher does not use the user argument. Derive once, before
        // acquiring the identity gate and before execution-strategy retries.
        var passwordHash = passwords.HashPassword(null!, command.Password);
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ChangeIdentityAsync(db, token);
            var committed = await db.AccountPurchaseRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId && x.AccountId == accountId && x.Purpose == "registration", token);
            if (committed?.ProofHash is { } committedProof && CryptographicOperations.FixedTimeEquals(committedProof, AccountSecrets.Hash(proof)))
                return requests.View(committed, command.Username, now, true, proof);
            await PostgresPurchaseRequests.ExpireRequestsAsync(db, now, token);
            var normalized = AccountRules.NormalizeUsername(command.Username);
            if (await db.AccountUsernameClaims.AnyAsync(x => x.NormalizedUsername == normalized, token))
                throw new AccountException("username_unavailable", "This username is already in use or temporarily reserved.", 409);
            var account = new AccountEntity
            {
                Id = accountId,
                Username = command.Username,
                NormalizedUsername = normalized,
                PasswordHash = passwordHash,
                CreatedAt = now
            };
            var request = new AccountPurchaseRequestEntity
            {
                Id = requestId,
                AccountId = accountId,
                Purpose = "registration",
                State = "awaiting_payment",
                ProofHash = AccountSecrets.Hash(proof),
                CodeImage = code,
                GeneratedAt = now,
                ExpiresAt = now.AddMinutes(10)
            };
            db.Accounts.Add(account); db.AccountWallets.Add(new() { AccountId = accountId }); db.AccountPurchaseRequests.Add(request);
            db.AccountUsernameClaims.Add(new() { NormalizedUsername = normalized, AccountId = accountId, RequestId = requestId });
            return requests.View(request, account.Username, now, true, proof);
        }, token);
    }

    public async Task<AccountPurchaseRequest> RegistrationAsync(Guid requestId, string proof, bool refresh, CancellationToken token)
    {
        await requests.CheckProofAsync(requestId, proof, token);
        if (refresh) await payments.RefreshRequestPaymentAsync(requestId, token);
        await requests.ExpireDueRequestsAsync(requestId, token);
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            var request = await db.AccountPurchaseRequests.SingleAsync(x => x.Id == requestId, token);
            var account = await db.Accounts.SingleAsync(x => x.Id == request.AccountId, token);
            return requests.View(request, account.Username, now);
        }, token);
    }
    public async Task<AccountPurchaseRequest> TopUpAsync(AccountSessionIdentity identity, CancellationToken token)
    {
        requests.RequirePayments();
        if (identity.AccountId is null) throw new AccountException("account_inactive", "Create your Snow Shot account in the desktop app first.", 403);
        await store.ThrottleAsync("topup:" + identity.AccountId.Value.ToString("N"), 10, token);
        var requestId = Guid.CreateVersion7(); var code = await platform.CodeAsync(requestId, token);
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            await RequireLiveSessionAsync(db, identity, now, token);
            var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == identity.AccountId && x.Active, token) ?? throw InvalidSession();
            var committed = await db.AccountPurchaseRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId && x.AccountId == account.Id && x.Purpose == "top_up", token);
            if (committed is not null) return requests.View(committed, account.Username, now, true);
            var request = new AccountPurchaseRequestEntity
            {
                Id = requestId,
                AccountId = account.Id,
                Purpose = "top_up",
                State = "awaiting_payment",
                CodeImage = code,
                GeneratedAt = now,
                ExpiresAt = now.AddMinutes(10)
            };
            db.AccountPurchaseRequests.Add(request); return requests.View(request, account.Username, now, true);
        }, token);
    }

    public async Task<AccountPurchaseRequest> RefreshTopUpAsync(AccountSessionIdentity identity, Guid requestId, CancellationToken token)
    {
        await using (var db = await store.OpenAsync(token))
            if (!await db.AccountPurchaseRequests.AnyAsync(x => x.Id == requestId && x.Purpose == "top_up" && x.AccountId == identity.AccountId, token)) throw NotFound();
        await payments.RefreshRequestPaymentAsync(requestId, token);
        await requests.ExpireDueRequestsAsync(requestId, token);
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            var request = await db.AccountPurchaseRequests.SingleAsync(x => x.Id == requestId, token);
            var username = await db.Accounts.Where(x => x.Id == request.AccountId).Select(x => x.Username).SingleAsync(token);
            return requests.View(request, username, now);
        }, token);
    }

    public async Task<AccountPurchaseRequest> CancelTopUpAsync(AccountSessionIdentity identity, Guid requestId, CancellationToken token)
    {
        return await store.ExecuteAsync(async (db, now) =>
        {
            // Serialize cancellation with checkout so an issued payment is never discarded.
            await AccountSynchronization.ChangeIdentityAsync(db, token);
            await RequireLiveSessionAsync(db, identity, now, token);
            var request = await db.AccountPurchaseRequests.SingleOrDefaultAsync(x => x.Id == requestId &&
                x.Purpose == "top_up" && x.AccountId == identity.AccountId, token) ?? throw NotFound();
            var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == request.AccountId && x.Active, token) ?? throw InvalidSession();
            if (request.State == "awaiting_payment" && request.OrderId is null)
                AccountPurchaseLifecycle.CancelUnissued(request);
            return requests.View(request, account.Username, now);
        }, token);
    }

    public async Task<PublicPurchaseRequest> PublicRequestAsync(Guid requestId, AccountSessionIdentity? identity, CancellationToken token)
    {
        await requests.ExpireDueRequestsAsync(requestId, token);
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            var request = await db.AccountPurchaseRequests.SingleOrDefaultAsync(x => x.Id == requestId, token) ?? throw NotFound();
            var username = await db.Accounts.Where(x => x.Id == request.AccountId).Select(x => x.Username).SingleAsync(token);
            var owns = identity?.AccountId == request.AccountId || (identity?.OpenId is not null &&
                (request.PayerOpenId == identity.OpenId || await db.AccountPaymentOrders.AnyAsync(x => x.RequestId == requestId && x.OpenId == identity.OpenId, token)));
            return requests.PublicView(request, username, now, owns);
        }, token);
    }
}
