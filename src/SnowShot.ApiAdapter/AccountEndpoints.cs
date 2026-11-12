using System.Text.Json;
using SnowShot.Api.Resources;
using SnowShot.Application;
using SnowShot.Contracts;

namespace SnowShot.Api;

internal static class AccountEndpoints
{
    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly HashSet<string> InferencePaths = new(StringComparer.OrdinalIgnoreCase)
    { "/api/v1/chat/completions", "/api/v2/translation/translate", "/api/v1/table/extract", "/api/v1/latex/extract" };

    internal static IApplicationBuilder UseAccountInferenceAuthentication(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/v2/auth") || context.Request.Path.StartsWithSegments("/api/v2/registration") ||
            context.Request.Path.StartsWithSegments("/api/v2/account") || context.Request.Path.StartsWithSegments("/api/v2/purchases"))
            context.Response.Headers.CacheControl = "no-store";
        if (InferencePaths.Contains((context.Request.Path.Value ?? string.Empty).TrimEnd('/')) && context.Request.Headers.ContainsKey("Authorization"))
        {
            var identity = await Identity(context);
            if (identity is null)
            {
                await ApiResponse.Problem(context, 401, "invalid_session", "Sign in again to continue.").ExecuteAsync(context); return;
            }
            if (identity.AccountId is { } id) context.Items["SnowShot.AccountId"] = id;
        }
        await next(context);
    });

    internal static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v2").WithTags("Account");
        group.MapGet("/registration/captcha", (HttpContext c, IAccountService s, CancellationToken t) => Run(c, () => s.CaptchaAsync(ClientKey(c), t))).Produces<AppEnvelope>();
        group.MapPost("/auth/login", async (HttpContext c, IAccountService s, PublicMessages m, CancellationToken t) =>
        {
            var body = await ApiResponse.ReadJsonAsync<AccountLoginRequest>(c, 8192, m["Validation failed"], t);
            return body.Error ?? await Run(c, () => s.LoginAsync(body.Value!.Username ?? "", body.Value.Password ?? "", ClientKey(c), t));
        }).Accepts<AccountLoginRequest>("application/json").Produces<AppEnvelope>();
        group.MapPost("/auth/wechat", async (HttpContext c, IAccountService s, PublicMessages m, CancellationToken t) =>
        {
            var body = await ApiResponse.ReadJsonAsync<AccountWechatLoginRequest>(c, 8192, m["Validation failed"], t);
            return body.Error ?? await Run(c, () => s.WechatLoginAsync(body.Value!.Code ?? "", ClientKey(c), t));
        }).Accepts<AccountWechatLoginRequest>("application/json").Produces<AppEnvelope>();
        group.MapPost("/auth/refresh", async (HttpContext c, IAccountService s, PublicMessages m, CancellationToken t) =>
        {
            var body = await ApiResponse.ReadJsonAsync<AccountRefreshRequest>(c, 8192, m["Validation failed"], t);
            return body.Error ?? await Run(c, () => s.RefreshSessionAsync(body.Value!.RefreshToken ?? "", t));
        }).Accepts<AccountRefreshRequest>("application/json").Produces<AppEnvelope>();
        group.MapPost("/auth/logout", async (HttpContext c, IAccountService s, PublicMessages m, CancellationToken t) =>
        {
            var body = await ApiResponse.ReadJsonAsync<AccountRefreshRequest>(c, 8192, m["Validation failed"], t);
            return body.Error ?? await Run(c, async () => { await s.LogoutAsync(body.Value!.RefreshToken ?? "", t); return new { logged_out = true }; });
        }).Accepts<AccountRefreshRequest>("application/json").Produces<AppEnvelope>();
        group.MapPost("/registration/requests", async (HttpContext c, IAccountService s, PublicMessages m, CancellationToken t) =>
        {
            var body = await ApiResponse.ReadJsonAsync<AccountRegistrationRequest>(c, 16384, m["Validation failed"], t);
            return body.Error ?? await Run(c, () => s.RegisterAsync(new(body.Value!.Username ?? "", body.Value.Password ?? "",
                body.Value.PasswordConfirmation ?? "", body.Value.CaptchaId, body.Value.CaptchaAnswer ?? ""), ClientKey(c), t, AcceptsJpeg(c)));
        }).Accepts<AccountRegistrationRequest>("application/json").Produces<AppEnvelope>();
        group.MapGet("/registration/requests/{id:guid}", (Guid id, HttpContext c, IAccountService s, CancellationToken t) => Run(c, () => s.RegistrationAsync(id, Proof(c), false, t))).Produces<AppEnvelope>();
        group.MapPost("/registration/requests/{id:guid}/refresh", (Guid id, HttpContext c, IAccountService s, CancellationToken t) => Run(c, () => s.RegistrationAsync(id, Proof(c), true, t))).Produces<AppEnvelope>();
        group.MapPost("/registration/requests/{id:guid}/claim", (Guid id, HttpContext c, IAccountService s, CancellationToken t) => Run(c, () => s.ClaimAsync(id, Proof(c), t))).Produces<AppEnvelope>();
        group.MapPost("/registration/requests/{id:guid}/link", (Guid id, HttpContext c, IAccountService s, CancellationToken t) => Auth(c, i => s.LinkRegistrationAsync(i, id, t))).Produces<AppEnvelope>();
        group.MapGet("/purchases/packages", (HttpContext c, IAccountService s) => Success(s.Packages())).Produces<AppEnvelope>();
        group.MapPost("/purchases/requests", (HttpContext c, IAccountService s, CancellationToken t) => Auth(c, i => s.TopUpAsync(i, t, AcceptsJpeg(c)))).Produces<AppEnvelope>();
        group.MapPost("/purchases/mini-program/requests", (HttpContext c, IAccountService s, CancellationToken t) => Auth(c, i => s.MiniProgramPurchaseAsync(i, t))).Produces<AppEnvelope>();
        group.MapPost("/purchases/requests/{id:guid}/refresh", (Guid id, HttpContext c, IAccountService s, CancellationToken t) => Auth(c, i => s.RefreshTopUpAsync(i, id, t))).Produces<AppEnvelope>();
        group.MapPost("/purchases/requests/{id:guid}/cancel", (Guid id, HttpContext c, IAccountService s, CancellationToken t) => Auth(c, i => s.CancelTopUpAsync(i, id, t))).Produces<AppEnvelope>();
        group.MapGet("/purchases/requests/{id:guid}", async (Guid id, HttpContext c, IAccountService s, CancellationToken t) =>
        {
            var identity = await Identity(c);
            if (c.Request.Headers.ContainsKey("Authorization") && identity is null) return Invalid(c);
            return await Run(c, () => s.PublicRequestAsync(id, identity, t));
        }).Produces<AppEnvelope>();
        group.MapPost("/purchases/orders", async (HttpContext c, IAccountService s, PublicMessages m, CancellationToken t) =>
        {
            var body = await ApiResponse.ReadJsonAsync<AccountOrderRequest>(c, 8192, m["Validation failed"], t);
            return body.Error ?? await Auth(c, i => s.CreateOrderAsync(i, body.Value!.RequestId, body.Value.PackageId ?? "", t, body.Value.CheckoutAttemptId));
        }).Accepts<AccountOrderRequest>("application/json")
            .WithDescription("Provide package_id to create an order. Omit package_id and supply checkout_attempt_id to look up a retained order without issuing payment parameters.")
            .Produces<AppEnvelope>();
        group.MapPost("/purchases/orders/{id}/refresh", (string id, HttpContext c, IAccountService s, CancellationToken t) => Auth(c, i => s.RefreshOrderAsync(i, id, t))).Produces<AppEnvelope>();
        group.MapGet("/account", (HttpContext c, IAccountService s, CancellationToken t) => Auth(c, i => s.SummaryAsync(i, t))).Produces<AppEnvelope>();
        group.MapGet("/account/transactions", (HttpContext c, IAccountService s, string? cursor, int? limit, CancellationToken t) =>
            Auth(c, i => s.TransactionsAsync(i, cursor, limit ?? 20, t),
                transactions => ApiResponse.CompressibleJson(AppEnvelope.Success(transactions), SnakeJson))).Produces<AppEnvelope>();
        group.MapGet("/account/deletion", (HttpContext c, IAccountService s, CancellationToken t) => Auth(c, i => s.DeletionEligibilityAsync(i, t)))
            .WithDescription("Returns deletion eligibility and the opaque deletion_version snapshot. data.membership_tier is the linked account's advanced-card tier (cny-9-9, cny-29-9, cny-88-8, cny-188 or cny-999), calculated from cumulative net recharge under the same wallet lock; null means unlinked or below CNY 9.90. Existing exact nano-yuan balance fields remain available for settlement checks.")
            .Produces<AppEnvelope>();
        group.MapPost("/account/deletion", async (HttpContext c, IAccountService s, PublicMessages m, CancellationToken t) =>
        {
            var body = await ApiResponse.ReadJsonAsync<AccountDeletionRequest>(c, 8192, m["Validation failed"], t);
            return body.Error ?? await Auth(c, i => s.DeleteAsync(i, new(body.Value!.Confirmation ?? "", body.Value.WechatCode ?? "",
                body.Value.AcknowledgeForfeiture, body.Value.DeletionVersion ?? ""), t));
        }).Accepts<AccountDeletionRequest>("application/json").Produces<AppEnvelope>();
        group.MapMethods("/wechat/events", ["GET", "POST"], async (HttpContext c, IAccountService s, PublicMessages m, CancellationToken t) =>
        {
            try
            {
                string? body = null;
                if (HttpMethods.IsPost(c.Request.Method))
                {
                    var read = await ApiResponse.ReadBytesAsync(c, 128 * 1024, m["Validation failed"], t);
                    if (read.Error is not null) return read.Error;
                    body = System.Text.Encoding.UTF8.GetString(read.Value!);
                }
                var result = await s.VerifyCallbackAsync(c.Request.Query["signature"].ToString(), c.Request.Query["timestamp"].ToString(),
                    c.Request.Query["nonce"].ToString(), c.Request.Query["echostr"].ToString(), body, c.Request.Query["msg_signature"].ToString(), t);
                var contentType = result.StartsWith('<') ? "application/xml" : result.StartsWith('{') ? "application/json" : "text/plain";
                return Results.Text(result, contentType);
            }
            catch (AccountException error) { return ApiResponse.Problem(c, error.Status, error.Code, error.Message); }
        });
        return routes;
    }
    private static async Task<IResult> Auth<T>(HttpContext context, Func<AccountSessionIdentity, Task<T>> action, Func<T, IResult>? respond = null)
    {
        var identity = await Identity(context);
        return identity is null ? Invalid(context) : await Run(context, () => action(identity), respond);
    }
    private static async Task<AccountSessionIdentity?> Identity(HttpContext context)
    {
        var headers = context.Request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = value[7..];
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace)) return null;
        return await context.RequestServices.GetRequiredService<IAccountSessionResolver>().ResolveAsync(token, context.RequestAborted);
    }
    private static IResult Invalid(HttpContext c) => ApiResponse.Problem(c, 401, "invalid_session", "Sign in again to continue.");
    private static string ClientKey(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unavailable";
    private static string Proof(HttpContext c) => c.Request.Headers["X-Registration-Proof"].Count == 1 ? c.Request.Headers["X-Registration-Proof"].ToString() : "";
    private static bool AcceptsJpeg(HttpContext c) =>
        c.Request.Headers.TryGetValue("X-SnowShot-Accept-Jpeg", out var value) && value.Count == 1 && value[0] == "1";
    private static IResult Success<T>(T value) => Results.Json(AppEnvelope.Success(value!), SnakeJson);
    private static async Task<IResult> Run<T>(HttpContext context, Func<Task<T>> action, Func<T, IResult>? respond = null)
    {
        try
        {
            var value = await action();
            return respond is null ? Success(value) : respond(value);
        }
        catch (AccountException error) { return ApiResponse.Problem(context, error.Status, error.Code, error.Message); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
        { return ApiResponse.Problem(context, 503, "payments_unavailable", "The platform result is currently unavailable. Your order remains pending."); }
    }
}
