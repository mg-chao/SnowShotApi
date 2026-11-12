using Microsoft.AspNetCore.ResponseCompression;
using SnowShot.Api.Resources;

namespace SnowShot.Api;

public static class AdapterComposition
{
    public static IServiceCollection AddSnowShotApiAdapter(this IServiceCollection services)
    {
        services.AddSingleton<PublicMessages>();
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.MimeTypes = ["application/json"];
        });
        services.AddSingleton<IResponseCompressionProvider, LargeJsonCompressionProvider>();
        return services;
    }

    public static IApplicationBuilder UseSnowShotApiAdapter(this IApplicationBuilder application) =>
        application.UseMiddleware<ExceptionMiddleware>().UseAccountInferenceAuthentication().UseResponseCompression();

    public static IEndpointRouteBuilder MapSnowShotEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHealthEndpoints().MapChatEndpoints().MapTranslationEndpoints().MapTableEndpoints().MapLatexEndpoints().MapAccountEndpoints();
}
