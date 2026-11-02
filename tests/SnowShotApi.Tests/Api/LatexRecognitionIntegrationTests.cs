using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Providers;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShotApi.Tests.Api;

public sealed class LatexRecognitionIntegrationTests
{
    [Theory, Trait("Category", "RecognitionIntegration")]
    [InlineData("webp", "image/webp")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("png", "image/png")]
    public async Task RealFormulaTraversesMultipartApiAndWorkerWithoutRewriting(string format, string mediaType)
    {
        var url = Environment.GetEnvironmentVariable("SNOWSHOT_LATEX_TEST_WORKER_URL");
        var directory = Environment.GetEnvironmentVariable("SNOWSHOT_LATEX_TEST_IMAGE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(directory))
            Assert.Skip("Set SNOWSHOT_LATEX_TEST_WORKER_URL and SNOWSHOT_LATEX_TEST_IMAGE_DIRECTORY for real formula checks.");

        using var worker = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(120) };
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, $"fractions-light.{format}"), TestContext.Current.CancellationToken);
        using var rawImage = new ByteArrayContent(bytes);
        rawImage.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var direct = await worker.PostAsync("v2/latex/extract", rawImage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        using var expected = JsonDocument.Parse(await direct.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var expectedLatex = expected.RootElement.GetProperty("latex").GetString();
        Assert.Contains(@"\frac{a+b}{c-d}", expectedLatex);

        await using var original = new ApiFactory();
        await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILatexWorkerClient>();
            services.AddSingleton<ILatexWorkerClient>(provider => new LatexWorkerClient(
                new WorkerHttpClientFactory(worker), new LatexWorkerOptions { BaseUrl = url },
                provider.GetRequiredService<ServicePolicy>(), new DependencyHealth(TimeProvider.System), TimeProvider.System));
        }));
        using var client = factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(bytes);
        image.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(image, "image", $"formula.{format}");
        using var response = await client.PostAsync("/api/v1/latex/extract", form, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(expectedLatex, json.RootElement.GetProperty("data").GetProperty("latex").GetString());
        Assert.Equal(new NanoYuan(15_000_000), Assert.Single(original.Ledger.Settlements).ReportedPublicCost);
    }

    private sealed class WorkerHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
