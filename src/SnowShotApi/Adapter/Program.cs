using System.Globalization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SnowShot.Api;
using SnowShot.Infrastructure;
using SnowShot.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddMountedSecrets(builder.Environment.EnvironmentName);
builder.Services.AddSnowShotApiAdapter();
builder.Services.AddOpenApi();
var otlpConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("SnowShotApi"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource("SnowShot");
        if (otlpConfigured) tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddMeter("SnowShot");
        if (otlpConfigured) metrics.AddOtlpExporter();
    });
builder.Services.AddSnowShotInfrastructure(builder.Configuration, builder.Environment);
var deployedEnvironment = !builder.Environment.IsDevelopment();
var parsedProxies = IngressPolicy.Validate(builder.Configuration, deployedEnvironment);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 2;
    options.RequireHeaderSymmetry = true;
    foreach (var address in parsedProxies) options.KnownProxies.Add(address);
});

var app = builder.Build();
app.UseForwardedHeaders();
if (deployedEnvironment)
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        await next(context);
    });
}
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("zh-CN"),
    SupportedCultures = [new CultureInfo("zh-CN"), new CultureInfo("en-US")],
    SupportedUICultures = [new CultureInfo("zh-CN"), new CultureInfo("en-US")],
    RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()],
});
app.UseSnowShotApiAdapter();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapSnowShotEndpoints();
app.Run();

public partial class Program;
