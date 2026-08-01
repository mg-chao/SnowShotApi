using SnowShot.Api.Resources;

namespace SnowShot.Api;

public static class AdapterComposition
{
    public static IServiceCollection AddSnowShotApiAdapter(this IServiceCollection services)
    {
        services.AddSingleton<PublicMessages>();
        return services;
    }

    public static IApplicationBuilder UseSnowShotApiAdapter(this IApplicationBuilder application) =>
        application.UseMiddleware<ExceptionMiddleware>();

    public static IEndpointRouteBuilder MapSnowShotEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHealthEndpoints().MapChatEndpoints().MapTranslationEndpoints().MapTableEndpoints();
}
