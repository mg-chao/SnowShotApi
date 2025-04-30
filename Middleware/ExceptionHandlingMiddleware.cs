using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using SnowShotApi.Controllers;
using SnowShotApi.Resources;

namespace SnowShotApi.Middleware;

public class ExceptionHandlingMiddleware(
    RequestDelegate next,
    IWebHostEnvironment env,
    IStringLocalizer<SharedResource> localizer)
{
    private readonly RequestDelegate _next = next;
    private readonly IWebHostEnvironment _env = env;

    private readonly IStringLocalizer<SharedResource> _localizer = localizer;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (InvalidOperationException ex)
        {
            if (ex.InnerException is Npgsql.NpgsqlException)
            {
                await HandleDatabaseExceptionAsync(context, ex);
            }
            else
            {
                await HandleExceptionAsync(context, ex);
            }
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleDatabaseExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";

        var response = new AppResponse(
            1101,
            _env.IsDevelopment() ? exception.Message : _localizer["Database connection error"],
            _env.IsDevelopment() ? new { exception.StackTrace } : null
        );

        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";

        var response = new AppResponse(
            9999,
            _env.IsDevelopment() ? exception.Message : _localizer["Internal server error"],
            _env.IsDevelopment() ? new { exception.StackTrace } : null
        );

        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
}