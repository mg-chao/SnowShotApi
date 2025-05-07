using Microsoft.EntityFrameworkCore;
using SnowShotApi.Data;
using DotNetEnv;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using SnowShotApi.Services.TranslationServices;
using SnowShotApi.Services.UserServices;
using SnowShotApi.Middleware;
using SnowShotApi.Services.OrderServices;
using SnowShotApi.Services.ChatServices;

Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    // User Database
    options.UseNpgsql($"Host={Env.GetString("DB_HOST")};Port={Env.GetString("DB_PORT")};Database={Env.GetString("DB_USER_DATABASE")};Username={Env.GetString("DB_USER_NAME")};Password={Env.GetString("DB_USER_PASSWORD")}"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddControllers();
builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SnowShot API", Version = "v1" });
});

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddMvc()
    .AddDataAnnotationsLocalization();

builder.Services.AddHttpClient();
builder.Services.AddScoped<IIpUserService, IpUserService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ITranslationOrderService, TranslationOrderService>();
builder.Services.AddScoped<ITranslationService, TranslationService>();
builder.Services.AddScoped<IYoudaoTranslationService, YoudaoTranslationService>();
builder.Services.AddScoped<ITranslationOrderStatsService, TranslationOrderStatsService>();
builder.Services.AddScoped<IChatOrderStatsService, ChatOrderStatsService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IChatOrderService, ChatOrderService>();
builder.Services.AddScoped<IDeepseekService, DeepseekService>();
builder.Services.AddScoped<IClaudeService, ClaudeService>();
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

