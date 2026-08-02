using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Providers;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShotApi.Tests.Domain;

public sealed class ProviderContractTests
{
    [Fact]
    public void CloudProviderSettingsAreSharedAcrossModelAccesses()
    {
        var catalog = Catalog();

        var flash = catalog.Get(Resources.QwenFlash, "test");
        var plus = catalog.Get(Resources.QwenPlus, "test");

        Assert.Equal(flash.Endpoint, plus.Endpoint);
        Assert.Equal(flash.ApiKey, plus.ApiKey);
        Assert.Equal("test-model", flash.Selection.UpstreamModel);
        Assert.Equal("test-plus", plus.Selection.UpstreamModel);
    }

    [Fact]
    public void OneLogicalModelCanUseMultipleCentralizedCloudProviders()
    {
        var catalog = new ProviderModelCatalog(new ProviderModelsOptions
        {
            CloudProviders = new Dictionary<string, CloudProviderOptions>(StringComparer.Ordinal)
            {
                ["aliyun"] = new() { Endpoint = "https://aliyun.test/chat", ApiKey = "aliyun-key" },
                ["deepseek"] = new() { Endpoint = "https://deepseek.test/chat", ApiKey = "deepseek-key" },
            },
            Models = new Dictionary<string, ProviderModelOptions>(StringComparer.Ordinal)
            {
                [Resources.DeepSeekV4] = new()
                {
                    Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
                    {
                        ["aliyun"] = new() { Provider = "aliyun", UpstreamModel = "deepseek-v4-flash", MaxConcurrentRequests = 16 },
                        ["deepseek"] = new() { Provider = "deepseek", UpstreamModel = "deepseek-v4-flash", MaxConcurrentRequests = 16 },
                    },
                },
            },
        }, new TranslationProviderOptions { LogicalModels = [Resources.DeepSeekV4] }, requireHttps: true);

        var aliyun = catalog.Get(Resources.DeepSeekV4, "aliyun");
        var deepseek = catalog.Get(Resources.DeepSeekV4, "deepseek");

        Assert.Equal("https://aliyun.test/chat", aliyun.Endpoint.ToString());
        Assert.Equal("aliyun-key", aliyun.ApiKey);
        Assert.Equal("https://deepseek.test/chat", deepseek.Endpoint.ToString());
        Assert.Equal("deepseek-key", deepseek.ApiKey);
        var model = Assert.Single(catalog.Models);
        Assert.Equal(Resources.DeepSeekV4, model.Model);
        Assert.False(model.SupportVision);
    }

    [Fact]
    public void TranslationModelsMustBeUniqueAndConfigured()
    {
        var providers = new ProviderModelsOptions
        {
            CloudProviders = new Dictionary<string, CloudProviderOptions>(StringComparer.Ordinal)
            {
                ["test"] = new() { Endpoint = "https://provider.test/chat", ApiKey = "key" },
            },
            Models = new Dictionary<string, ProviderModelOptions>(StringComparer.Ordinal)
            {
                [Resources.QwenPlus] = new()
                {
                    Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
                    {
                        ["test"] = new() { Provider = "test", UpstreamModel = "qwen-plus", MaxConcurrentRequests = 1 },
                    },
                },
            },
        };

        Assert.Throws<InvalidOperationException>(() => new ProviderModelCatalog(providers,
            new TranslationProviderOptions(), requireHttps: true));
        Assert.Throws<InvalidOperationException>(() => new ProviderModelCatalog(providers,
            new TranslationProviderOptions { LogicalModels = [Resources.QwenPlus, Resources.QwenPlus] }, requireHttps: true));
        Assert.Throws<InvalidOperationException>(() => new ProviderModelCatalog(providers,
            new TranslationProviderOptions { LogicalModels = [Resources.DeepSeekV4] }, requireHttps: true));
    }

    [Fact]
    public async Task TranslationForwardsOnlyTheInternalOperationId()
    {
        const string payload = "{\"choices\":[{\"message\":{\"content\":\"{\\\"translations\\\":[{\\\"index\\\":0,\\\"content\\\":\\\"hola\\\"}]}\"}}]}";
        var handler = new ResponseHandler(payload, "application/json");
        var catalog = Catalog();
        var client = new OpenAiTranslationClient(
            new SingleClientRegistry(new HttpClient(handler)),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, catalog,
            ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(["hola"], result.Results);
        Assert.True(handler.SawOperationId);
        Assert.False(handler.SawClientRequestId);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(request.RootElement.TryGetProperty("enable_thinking", out _));
        using var user = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var item = Assert.Single(user.RootElement.EnumerateArray());
        Assert.Equal(0, item.GetProperty("index").GetInt32());
        Assert.Equal("hello", item.GetProperty("content").GetString());
    }

    [Fact]
    public async Task TranslationEmitsThinkingSettingOnlyWhenProviderConfiguresIt()
    {
        const string payload = "{\"choices\":[{\"message\":{\"content\":\"{\\\"translations\\\":[{\\\"index\\\":0,\\\"content\\\":\\\"hola\\\"}]}\"}}]}";
        var handler = new ResponseHandler(payload, "application/json");
        var client = new OpenAiTranslationClient(new SingleClientRegistry(new HttpClient(handler)),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, Catalog(false),
            ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(request.RootElement.GetProperty("enable_thinking").GetBoolean());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task TranslationClassifiesHttpFailures(HttpStatusCode status, bool retryable)
    {
        var handler = new ResponseHandler("failure", "text/plain", status);
        var client = TranslationClient(handler);

        var result = await client.TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal((int)status, result.Attempt.HttpStatus);
    }

    [Fact]
    public async Task TranslationParsesRetryAfterDeltaAndIgnoresInvalidValues()
    {
        var valid = await TranslationClient(new ResponseHandler("failure", "text/plain",
            HttpStatusCode.TooManyRequests, "17")).TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);
        var invalid = await TranslationClient(new ResponseHandler("failure", "text/plain",
            HttpStatusCode.TooManyRequests, "not-a-delay")).TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(17), valid.RetryAfter);
        Assert.Null(invalid.RetryAfter);
    }

    [Fact]
    public async Task TranslationParsesFutureAndExpiredRetryAfterDates()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var future = await TranslationClient(new ResponseHandler("failure", "text/plain", HttpStatusCode.TooManyRequests,
            now.AddSeconds(31).ToString("R", CultureInfo.InvariantCulture)), time)
            .TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);
        var expired = await TranslationClient(new ResponseHandler("failure", "text/plain", HttpStatusCode.TooManyRequests,
            now.AddSeconds(-31).ToString("R", CultureInfo.InvariantCulture)), time)
            .TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(31), future.RetryAfter);
        Assert.Equal(TimeSpan.Zero, expired.RetryAfter);
    }

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"message\":{}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"not-json\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"{\\\"translations\\\":[]}\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"{\\\"translations\\\":[{\\\"index\\\":0,\\\"content\\\":\\\"one\\\"},{\\\"index\\\":1,\\\"content\\\":\\\"two\\\"}]}\"}}]}")]
    public async Task MalformedTranslationCompletionsAreRetryable(string payload)
    {
        var result = await TranslationClient(new ResponseHandler(payload, "application/json"))
            .TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.Retryable);
        Assert.Contains(result.Outcome, new[] { "invalid_output", "invalid_response" });
    }

    [Fact]
    public async Task TranslationAcceptsOutputLargerThanFormerCharacterLimit()
    {
        var translated = new string('a', 6_000);
        var translatedPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            translations = new[] { new { index = 0, content = translated } },
        });
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = translatedPayload } } },
        });
        var handler = new ResponseHandler(payload, "application/json");
        var client = new OpenAiTranslationClient(
            new SingleClientRegistry(new HttpClient(handler)),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, Catalog(),
            ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(translated, Assert.Single(result.Results));
    }

    [Fact]
    public async Task OverflowingChatUsageBecomesInvalidStreamFailure()
    {
        const string payload = "data: {\"usage\":{\"prompt_tokens\":9223372036854775807,\"completion_tokens\":1,\"total_tokens\":9223372036854775807}}\n\n";
        var handler = new ResponseHandler(payload);
        var catalog = Catalog();
        var client = new OpenAiChatClient(
            new SingleClientRegistry(new HttpClient(handler)),
            new ChatProviderOptions(), catalog,
            ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var events = new List<ChatProviderEvent>();

        await foreach (var providerEvent in client.StreamAsync(Command(), TestContext.Current.CancellationToken))
            events.Add(providerEvent);

        var failure = Assert.IsType<ChatProviderEvent.Failure>(Assert.Single(events));
        Assert.Equal("invalid_stream", failure.Category);
        Assert.False(failure.Attempt.CostKnown);
        Assert.Equal(AttemptDispatchState.Dispatched, failure.Attempt.DispatchState);
        Assert.True(handler.SawOperationId);
        Assert.False(handler.SawClientRequestId);
    }

    [Fact]
    public async Task ChatForwardsUnknownFieldsAndControlsRoutingAndUsageSettings()
    {
        const string response = "data: {\"id\":\"one\",\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}\n\n" +
            "data: [DONE]\n\n";
        var handler = new ResponseHandler(response);
        var client = new OpenAiChatClient(new SingleClientRegistry(new HttpClient(handler)), new ChatProviderOptions(),
            Catalog(), ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var command = Command() with
        {
            Request = new ChatCommand(Resources.QwenFlash, """
                {"model":"public-name","messages":[{"role":"user","content":"hello"}],"temperature":99,
                 "vendor_option":{"enabled":true},"stream":false,
                 "stream_options":{"include_usage":false,"vendor_detail":true}}
                """u8.ToArray()),
        };
        var events = new List<ChatProviderEvent>();

        await foreach (var providerEvent in client.StreamAsync(command, TestContext.Current.CancellationToken))
            events.Add(providerEvent);

        Assert.IsType<ChatProviderEvent.Terminal>(events[^1]);
        using var forwarded = System.Text.Json.JsonDocument.Parse(handler.RequestBody!);
        var root = forwarded.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("vendor_detail").GetBoolean());
        Assert.Equal(99, root.GetProperty("temperature").GetInt32());
        Assert.True(root.GetProperty("vendor_option").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ProviderClientsTreatEveryServerErrorAsRetryable()
    {
        var handler = new ResponseHandler("unavailable", "text/plain", HttpStatusCode.NotImplemented);
        var registry = new SingleClientRegistry(new HttpClient(handler));
        var catalog = Catalog();
        var health = new DependencyHealth(TimeProvider.System);
        var policy = ServicePolicy.Defaults();
        var chat = new OpenAiChatClient(registry, new ChatProviderOptions(), catalog, policy, health, TimeProvider.System);
        var translation = new OpenAiTranslationClient(registry,
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, catalog, policy, health, TimeProvider.System);

        var chatEvents = new List<ChatProviderEvent>();
        await foreach (var providerEvent in chat.StreamAsync(Command(), TestContext.Current.CancellationToken))
            chatEvents.Add(providerEvent);
        var chatFailure = Assert.IsType<ChatProviderEvent.Failure>(Assert.Single(chatEvents));
        var translationFailure = await translation.TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.True(chatFailure.Retryable);
        Assert.True(translationFailure.Retryable);
    }

    private static ChatProviderCommand Command()
    {
        var policy = ServicePolicy.Defaults();
        var resource = policy.Get(Resources.QwenFlash);
        var snapshot = new ReservationSnapshot(policy.Revision, policy.Fingerprint, Resources.QwenFlash, resource.Price,
            policy.PrincipalDailyAllowance, new NanoYuan(1_000_000), resource.OperatorMaximum);
        var handle = new OperationHandle(Guid.CreateVersion7(), new byte[32], 1,
            DateTimeOffset.UtcNow.AddMinutes(1), snapshot);
        var access = new ProviderAccessSelection(Resources.QwenFlash, "test", "test", "test-model");
        return new(handle, new ChatCommand(Resources.QwenFlash,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { model = Resources.QwenFlash, messages = Array.Empty<object>() })),
            access, "request-1", "trace-1", 1, Guid.CreateVersion7(), DateTimeOffset.UtcNow);
    }

    private static TranslationProviderCommand TranslationCommand()
    {
        var policy = ServicePolicy.Defaults();
        var resource = policy.Get(Resources.Translation);
        var snapshot = new ReservationSnapshot(policy.Revision, policy.Fingerprint, Resources.Translation, resource.Price,
            policy.PrincipalDailyAllowance, new NanoYuan(1_000_000), resource.OperatorMaximum);
        var handle = new OperationHandle(Guid.CreateVersion7(), new byte[32], 1,
            DateTimeOffset.UtcNow.AddMinutes(1), snapshot);
        var access = new ProviderAccessSelection(Resources.QwenFlash, "test", "test", "test-model");
        return new("hello", "en", "es", "general", access, handle, "request-1", "trace-1", 1,
            0, 1, 1, Guid.CreateVersion7(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    private static OpenAiTranslationClient TranslationClient(ResponseHandler handler, TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        return new(new SingleClientRegistry(new HttpClient(handler)),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, Catalog(),
            ServicePolicy.Defaults(), new DependencyHealth(time), time);
    }

    private static ProviderModelCatalog Catalog(bool? translationEnableThinking = null)
    {
        ProviderModelOptions Model(string upstream) => new()
        {
            Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
            {
                ["test"] = new() { Provider = "test", UpstreamModel = upstream, MaxConcurrentRequests = 16 },
            },
        };
        return new(new ProviderModelsOptions
        {
            CloudProviders = new Dictionary<string, CloudProviderOptions>(StringComparer.Ordinal)
            {
                ["test"] = new()
                {
                    Endpoint = "https://provider.test/chat",
                    ApiKey = "test",
                    TranslationEnableThinking = translationEnableThinking,
                },
            },
            Models = new Dictionary<string, ProviderModelOptions>(StringComparer.Ordinal)
            {
                [Resources.QwenFlash] = Model("test-model"),
                [Resources.QwenPlus] = Model("test-plus"),
                [Resources.QwenVisionFlash] = Model("test-vision"),
            },
        }, new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, requireHttps: true);
    }

    private sealed class SingleClientRegistry(HttpClient client) : IProviderHttpClientRegistry
    {
        public HttpClient CreateClient(ProviderAccessSelection selection) => client;
    }

    private sealed class ResponseHandler(string payload, string mediaType = "text/event-stream",
        HttpStatusCode statusCode = HttpStatusCode.OK, string? retryAfter = null) : HttpMessageHandler
    {
        public bool SawOperationId { get; private set; }
        public bool SawClientRequestId { get; private set; }
        public byte[]? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SawOperationId = request.Headers.Contains("X-Operation-ID");
            SawClientRequestId = request.Headers.Contains("X-Request-ID");
            RequestBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(payload, Encoding.UTF8, mediaType),
            };
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
