using SnowShot.Application;

namespace SnowShot.Infrastructure.Accounts;

internal static class AccountErrors
{
    internal static AccountException Credentials() => new("invalid_credentials", "The username or password is incorrect.", 401);
    internal static AccountException InvalidSession() => new("invalid_session", "Sign in again to continue.", 401);
    internal static AccountException NotFound() => new("request_not_found", "This purchase request was not found.", 404);
    internal static AccountException LinkConflict() => new("wechat_link_conflict", "This WeChat identity or Snow Shot account is already linked or reserved for another account.", 409);
}
