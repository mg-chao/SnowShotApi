using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace SnowShot.Api;

internal sealed class LargeJsonCompressionProvider(IServiceProvider services, IOptions<ResponseCompressionOptions> options)
    : IResponseCompressionProvider
{
    internal const int MinimumBytes = 1024;
    internal static readonly object EligibleResponse = new();
    private readonly ResponseCompressionProvider provider = new(services, options);

    public bool CheckRequestAcceptsCompression(HttpContext context) => provider.CheckRequestAcceptsCompression(context);
    public ICompressionProvider? GetCompressionProvider(HttpContext context) => provider.GetCompressionProvider(context);

    public bool ShouldCompressResponse(HttpContext context) =>
        context.Items.ContainsKey(EligibleResponse) && context.Response.ContentLength >= MinimumBytes &&
        provider.ShouldCompressResponse(context);
}
