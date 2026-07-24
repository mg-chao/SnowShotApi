using System.Globalization;
using DotNetEnv;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SnowShotApi.Controllers;
using SnowShotApi.Data;
using SnowShotApi.Middleware;
using SnowShotApi.Services.ChatServices;
using SnowShotApi.Services.OrderServices;
using SnowShotApi.Services.TranslationServices;
using SnowShotApi.Services.TranslationServices.AI;
using SnowShotApi.Services.UserServices;

Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(
        $"Host={Env.GetString("DB_HOST")};Port={Env.GetString("DB_PORT")};Database={Env.GetString("DB_USER_DATABASE")};Username={Env.GetString("DB_USER_NAME")};Password={Env.GetString("DB_USER_PASSWORD")}"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddControllers();
builder.Services.AddRouting(options => options.LowercaseUrls = true);

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .SelectMany(entry => entry.Value!.Errors)
            .Select(error => error.ErrorMessage)
            .ToList();

        var errorMessage = errors.Count > 0 ? string.Join("; ", errors) : "Validation failed";
        return new OkObjectResult(new AppResponse(40001, errorMessage));
    };
});

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "SnowShot API", Version = "v1" });
});

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddMvc().AddDataAnnotationsLocalization();
builder.Services.AddAITranslation(builder.Configuration, builder.Environment);

builder.Services.AddHttpClient();
builder.Services.AddScoped<IIpUserService, IpUserService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IChatOrderStatsService, ChatOrderStatsService>();
builder.Services.AddScoped<IChatOrderService, ChatOrderService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<ITranslationOrderStatsService, TranslationOrderStatsService>();
builder.Services.AddScoped<ITranslationOrderService, TranslationOrderService>();
builder.Services.AddScoped<ITranslationService, TranslationService>();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

var supportedCultures = new[]
{
    new CultureInfo("zh-CN"),
    new CultureInfo("en-US"),
};

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("zh-CN"),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedCultures,
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "SnowShot API V1");
    });
}

app.UseHttpsRedirection();
app.MapControllers();
app.Run();
