using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SnowShot.Application;
using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

internal static class AccountTestServices
{
    internal static PostgresAccountService Create(IDbContextFactory<SnowShotDbContext> factory,
        IWechatAccountPlatform platform, AccountSecrets secrets, TimeProvider clock, WechatAccountOptions options)
    {
        var passwords = new PasswordHasher<AccountEntity>(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
        var store = new PostgresAccountStore(factory, secrets);
        var requests = new PostgresPurchaseRequests(store, platform, clock);
        var inbox = new PostgresAccountEventInbox(store, secrets, NullLogger<PostgresAccountEventInbox>.Instance);
        var sessions = new PostgresAccountSessions(store, platform, secrets, clock, passwords, requests);
        var payments = new PostgresAccountPayments(store, platform, secrets, clock, requests, inbox,
            new PostgresAccountRetryQueue(factory), NullLogger<PostgresAccountPayments>.Instance);
        var purchases = new PostgresAccountPurchases(store, platform, secrets, clock, options, passwords, requests, payments);
        var deletion = new PostgresAccountDeletion(store, platform, secrets, inbox);
        return new(store, platform, options, sessions, purchases, payments, deletion);
    }
}
