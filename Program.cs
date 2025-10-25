using Microsoft.EntityFrameworkCore;
using SnowShotApi.Data;
using DotNetEnv;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using SnowShotApi.Services.UserServices;
using SnowShotApi.Middleware;
using SnowShotApi.Services.OrderServices;
using SnowShotApi.Services.ChatServices;
using SnowShotApi.Services.TranslationServices;
using Microsoft.AspNetCore.Mvc;
using SnowShotApi.Controllers;

Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    // User Database
    options.UseNpgsql($"Host={Env.GetString("DB_HOST")};Port={Env.GetString("DB_PORT")};Database={Env.GetString("DB_USER_DATABASE")};Username={Env.GetString("DB_USER_NAME")};Password={Env.GetString("DB_USER_PASSWORD")}"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddControllers();
builder.Services.AddRouting(options => options.LowercaseUrls = true);

// 配置模型验证失败时的响应格式
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .SelectMany(e => e.Value!.Errors)
            .Select(e => e.ErrorMessage)
            .ToList();

        var errorMessage = errors.Count > 0 ? string.Join("; ", errors) : "Validation failed";
        
        var response = new AppResponse(40001, errorMessage);
        
        return new OkObjectResult(response);
    };
});

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SnowShot API", Version = "v1" });
});

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddMvc()
    .AddDataAnnotationsLocalization();

// 配置 HttpClient，解决 SSL 连接问题
builder.Services.AddHttpClient<IYoudaoTranslationService, YoudaoTranslationService>()
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new HttpClientHandler();
        // 在开发环境中，可以跳过 SSL 证书验证（生产环境不推荐）
        if (builder.Environment.IsDevelopment())
        {
            handler.ServerCertificateCustomValidationCallback = 
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return handler;
    });

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

// 添加异常处理中间件
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
    SupportedUICultures = supportedCultures
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "SnowShot API V1");
    });
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();

