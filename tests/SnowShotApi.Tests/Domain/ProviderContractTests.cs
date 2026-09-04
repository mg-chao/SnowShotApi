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
        var mt = catalog.Get(Resources.QwenMtFlash, "test");

        Assert.Equal(flash.Endpoint, mt.Endpoint);
        Assert.Equal(flash.ApiKey, mt.ApiKey);
        Assert.Equal("test-model", flash.Selection.UpstreamModel);
        Assert.Equal("test-mt", mt.Selection.UpstreamModel);
    }

    [Fact]
    public void TranslationDesignatedModelsAreFlaggedInTheChatCatalog()
    {
        var catalog = Catalog();

        Assert.Equal([Resources.QwenMtFlash, Resources.QwenVisionFlash, Resources.QwenFlash],
            catalog.Models.Select(model => model.Model).ToArray());
        Assert.Equal([true, false, false],
            catalog.Models.Select(model => model.Translation).ToArray());
        Assert.True(catalog.Contains(Resources.QwenFlash));
        Assert.True(catalog.Contains(Resources.QwenVisionFlash));
        Assert.True(catalog.Contains(Resources.QwenMtFlash));
        Assert.True(catalog.IsTranslationModel(Resources.QwenMtFlash));
        Assert.False(catalog.IsTranslationModel(Resources.QwenFlash));
        Assert.Equal("test-mt", catalog.Get(Resources.QwenMtFlash, "test").Selection.UpstreamModel);
        Assert.Equal([Resources.QwenMtFlash], catalog.Selections(Resources.QwenMtFlash)
            .Select(selection => selection.LogicalModel).ToArray());
    }

    [Fact]
    public void TranslationOnlyCatalogStillExposesChatModels()
    {
        var providers = new ProviderModelsOptions
        {
            CloudProviders = new Dictionary<string, CloudProviderOptions>(StringComparer.Ordinal)
            {
                ["test"] = new() { Endpoint = "https://provider.test/chat", ApiKey = "key" },
            },
            Models = new Dictionary<string, ProviderModelOptions>(StringComparer.Ordinal)
            {
                [Resources.QwenMtFlash] = new()
                {
                    Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
                    {
                        ["test"] = new() { Provider = "test", UpstreamModel = "qwen-mt-flash", MaxConcurrentRequests = 1 },
                    },
                },
            },
        };

        var catalog = new ProviderModelCatalog(providers,
            new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash] }, requireHttps: true);

        var model = Assert.Single(catalog.Models);
        Assert.Equal(Resources.QwenMtFlash, model.Model);
        Assert.True(model.Translation);
        Assert.True(catalog.Contains(Resources.QwenMtFlash));
    }

    [Fact]
    public void OneLogicalModelCanUseMultipleCentralizedCloudProviders()
    {
        var catalog = new ProviderModelCatalog(new ProviderModelsOptions
        {
            CloudProviders = new Dictionary<string, CloudProviderOptions>(StringComparer.Ordinal)
            {
                ["primary"] = new() { Endpoint = "https://primary.test/chat", ApiKey = "primary-key" },
                ["secondary"] = new() { Endpoint = "https://secondary.test/chat", ApiKey = "secondary-key" },
            },
            Models = new Dictionary<string, ProviderModelOptions>(StringComparer.Ordinal)
            {
                [Resources.QwenVisionFlash] = new()
                {
                    SupportVision = true,
                    Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
                    {
                        ["primary"] = new() { Provider = "primary", UpstreamModel = "qwen3-vl-flash", MaxConcurrentRequests = 16 },
                        ["secondary"] = new() { Provider = "secondary", UpstreamModel = "qwen3-vl-flash", MaxConcurrentRequests = 16 },
                    },
                },
                [Resources.QwenMtFlash] = new()
                {
                    Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
                    {
                        ["primary"] = new() { Provider = "primary", UpstreamModel = "qwen-mt-flash", MaxConcurrentRequests = 16 },
                    },
                },
            },
        }, new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash] }, requireHttps: true);

        var primary = catalog.Get(Resources.QwenVisionFlash, "primary");
        var secondary = catalog.Get(Resources.QwenVisionFlash, "secondary");

        Assert.Equal("https://primary.test/chat", primary.Endpoint.ToString());
        Assert.Equal("primary-key", primary.ApiKey);
        Assert.Equal("https://secondary.test/chat", secondary.Endpoint.ToString());
        Assert.Equal("secondary-key", secondary.ApiKey);
        var model = Assert.Single(catalog.Models, definition => definition.Model == Resources.QwenVisionFlash);
        Assert.True(model.SupportVision);
        Assert.False(model.Translation);
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
                [Resources.QwenMtFlash] = new()
                {
                    Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
                    {
                        ["test"] = new() { Provider = "test", UpstreamModel = "qwen-mt-flash", MaxConcurrentRequests = 1 },
                    },
                },
            },
        };

        Assert.Throws<InvalidOperationException>(() => new ProviderModelCatalog(providers,
            new TranslationProviderOptions(), requireHttps: true));
        Assert.Throws<InvalidOperationException>(() => new ProviderModelCatalog(providers,
            new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash, Resources.QwenMtFlash] }, requireHttps: true));
        Assert.Throws<InvalidOperationException>(() => new ProviderModelCatalog(providers,
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, requireHttps: true));
    }

    [Fact]
    public void EmptyLogicalModelEntriesAreIgnoredSoOverlaysCanShrinkTheList()
    {
        var providers = new ProviderModelsOptions
        {
            CloudProviders = new Dictionary<string, CloudProviderOptions>(StringComparer.Ordinal)
            {
                ["test"] = new() { Endpoint = "https://provider.test/chat", ApiKey = "key" },
            },
            Models = new Dictionary<string, ProviderModelOptions>(StringComparer.Ordinal)
            {
                [Resources.QwenFlash] = new()
                {
                    Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
                    {
                        ["test"] = new() { Provider = "test", UpstreamModel = "qwen3.8-flash", MaxConcurrentRequests = 1 },
                    },
                },
                [Resources.QwenMtFlash] = new()
                {
                    Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
                    {
                        ["test"] = new() { Provider = "test", UpstreamModel = "qwen-mt-flash", MaxConcurrentRequests = 1 },
                    },
                },
            },
        };

        var shrunk = new ProviderModelCatalog(providers,
            new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash, ""] }, requireHttps: true);

        Assert.Equal([Resources.QwenMtFlash], shrunk.Selections(Resources.QwenMtFlash)
            .Select(selection => selection.LogicalModel).ToArray());
        Assert.True(shrunk.IsTranslationModel(Resources.QwenMtFlash));
        Assert.False(shrunk.IsTranslationModel(Resources.QwenFlash));

        Assert.Equal([Resources.QwenMtFlash],
            new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash, ""] }.ConfiguredLogicalModels);
        Assert.Empty(new TranslationProviderOptions { LogicalModels = ["", " "] }.ConfiguredLogicalModels);
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
        var message = Assert.Single(request.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var content = message.GetProperty("content").GetString()!;
        Assert.Contains("Return JSON only", content, StringComparison.Ordinal);
        using var user = JsonDocument.Parse(content[(content.LastIndexOf('\n') + 1)..]);
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

    [Fact]
    public async Task NativeTranslationSendsRawContentWithTranslationOptions()
    {
        const string payload = "{\"choices\":[{\"message\":{\"content\":\"hola\"}}]}";
        var handler = new ResponseHandler(payload, "application/json");
        var client = new OpenAiTranslationClient(new SingleClientRegistry(new HttpClient(handler)),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash] }, Catalog(),
            ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(NativeTranslationCommand(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(["hola"], result.Results);
        Assert.True(handler.SawOperationId);
        Assert.False(handler.SawClientRequestId);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        var root = request.RootElement;
        Assert.Equal("test-mt", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("enable_thinking", out _));
        Assert.False(root.TryGetProperty("response_format", out _));
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("hello", message.GetProperty("content").GetString());
        var options = root.GetProperty("translation_options");
        Assert.Equal("en", options.GetProperty("source_lang").GetString());
        Assert.Equal("es", options.GetProperty("target_lang").GetString());
        Assert.False(options.TryGetProperty("domains", out _));
    }

    [Fact]
    public async Task NativeTranslationEmitsProviderThinkingSetting()
    {
        const string payload = "{\"choices\":[{\"message\":{\"content\":\"hola\"}}]}";
        var handler = new ResponseHandler(payload, "application/json");
        var client = new OpenAiTranslationClient(new SingleClientRegistry(new HttpClient(handler)),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash] }, Catalog(false),
            ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(NativeTranslationCommand(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(request.RootElement.GetProperty("enable_thinking").GetBoolean());
    }

    [Theory]
    [InlineData("zh-CHS", "zh", "zh-CHT", "zh_tw", "computers", "IT domain")]
    [InlineData("zh-CHT", "zh_tw", "en", "en", "medicine", "medical domain")]
    [InlineData("en", "en", "zh-CHS", "zh", "finance", "finance domain")]
    [InlineData("en", "en", "zh-CHT", "zh_tw", "game", "game domain")]
    public async Task NativeTranslationMapsLanguagesAndDomains(
        string from, string sourceLang, string to, string targetLang, string domain, string domainPrompt)
    {
        const string payload = "{\"choices\":[{\"message\":{\"content\":\"hola\"}}]}";
        var handler = new ResponseHandler(payload, "application/json");
        var client = new OpenAiTranslationClient(new SingleClientRegistry(new HttpClient(handler)),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash] }, Catalog(),
            ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(NativeTranslationCommand() with
        {
            From = from, To = to, Domain = domain,
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        var options = request.RootElement.GetProperty("translation_options");
        Assert.Equal(sourceLang, options.GetProperty("source_lang").GetString());
        Assert.Equal(targetLang, options.GetProperty("target_lang").GetString());
        Assert.Contains(domainPrompt, options.GetProperty("domains").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeTranslationRejectsEmptyCompletionsAsInvalidOutput()
    {
        const string payload = "{\"choices\":[{\"message\":{\"content\":\"\"}}]}";
        var registry = new SingleClientRegistry(new HttpClient(new ResponseHandler(payload, "application/json")));
        var client = new OpenAiTranslationClient(registry,
            new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash] }, Catalog(),
            ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(NativeTranslationCommand(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.Retryable);
        Assert.Equal("invalid_output", result.Outcome);
        Assert.Equal([ProviderCircuitOutcome.TransientFailure], registry.Outcomes);
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
    public async Task TranslationReportsOnlyValidatedResponsesAsCircuitSuccess()
    {
        const string valid = "{\"choices\":[{\"message\":{\"content\":\"{\\\"translations\\\":[{\\\"index\\\":0,\\\"content\\\":\\\"hola\\\"}]}\"}}]}";
        var validRegistry = new SingleClientRegistry(new HttpClient(new ResponseHandler(valid, "application/json")));
        var invalidRegistry = new SingleClientRegistry(new HttpClient(new ResponseHandler("{\"choices\":[]}", "application/json")));
        var validClient = TranslationClient(validRegistry);
        var invalidClient = TranslationClient(invalidRegistry);

        Assert.True((await validClient.TranslateAsync(TranslationCommand(), CancellationToken.None)).Success);
        Assert.False((await invalidClient.TranslateAsync(TranslationCommand(), CancellationToken.None)).Success);

        Assert.Equal([ProviderCircuitOutcome.Success], validRegistry.Outcomes);
        Assert.Equal([ProviderCircuitOutcome.TransientFailure], invalidRegistry.Outcomes);
    }

    [Fact]
    public async Task TranslationReportsAttemptTimeoutButNotCallerCancellation()
    {
        var timeoutRegistry = new SingleClientRegistry(new HttpClient(new DelayingHandler()));
        var timeoutClient = TranslationClient(timeoutRegistry);
        var timedOut = await timeoutClient.TranslateAsync(
            TranslationCommand() with { Timeout = TimeSpan.FromMilliseconds(20) }, CancellationToken.None);

        var cancellationRegistry = new SingleClientRegistry(new HttpClient(new DelayingHandler()));
        var cancellationClient = TranslationClient(cancellationRegistry);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var cancelled = await cancellationClient.TranslateAsync(TranslationCommand(), cancellation.Token);

        Assert.Equal("attempt_timeout", timedOut.Outcome);
        Assert.Equal([ProviderCircuitOutcome.TransientFailure], timeoutRegistry.Outcomes);
        Assert.Equal("cancelled", cancelled.Outcome);
        Assert.Empty(cancellationRegistry.Outcomes);
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
        Assert.Equal(CostBasis.Estimated, failure.Attempt.Basis);
        Assert.True(failure.Attempt.InputUnits > 0);
        Assert.Equal(AttemptDispatchState.Dispatched, failure.Attempt.DispatchState);
        Assert.True(handler.SawOperationId);
        Assert.False(handler.SawClientRequestId);
    }

    [Fact]
    public async Task TruncatedChatStreamWithoutUsageSettlesFromDeliveredEvidence()
    {
        const string payload = "data: {\"id\":\"one\",\"choices\":[{\"delta\":{\"content\":\"hello world, this streamed\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking hard about the answer\"}}]}\n\n";
        var handler = new ResponseHandler(payload);
        var policy = PricedPolicy();
        var client = new OpenAiChatClient(new SingleClientRegistry(new HttpClient(handler)), new ChatProviderOptions(),
            Catalog(), policy, new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var events = new List<ChatProviderEvent>();

        await foreach (var providerEvent in client.StreamAsync(Command(), TestContext.Current.CancellationToken))
            events.Add(providerEvent);

        var failure = Assert.IsType<ChatProviderEvent.Failure>(events[^1]);
        Assert.Equal("truncated_stream", failure.Category);
        Assert.Equal(CostBasis.Estimated, failure.Attempt.Basis);
        Assert.Equal(AttemptDispatchState.Dispatched, failure.Attempt.DispatchState);
        Assert.True(failure.Attempt.InputUnits > 0);
        Assert.True(failure.Attempt.OutputUnits > 0);
        Assert.Equal(policy.Get(Resources.QwenFlash).Price.Calculate(failure.Attempt.InputUnits, failure.Attempt.OutputUnits),
            failure.Attempt.Cost);
    }

    [Fact]
    public async Task ChatOutputEstimationCountsUnicodeScalars()
    {
        const string payload = "data: {\"choices\":[{\"delta\":{\"content\":\"😀😀\"}}]}\n\n";
        var handler = new ResponseHandler(payload);
        var policy = PricedPolicy(ratios: new EstimationRatios(3, 3));
        var client = new OpenAiChatClient(new SingleClientRegistry(new HttpClient(handler)), new ChatProviderOptions(),
            Catalog(), policy, new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var events = new List<ChatProviderEvent>();

        await foreach (var providerEvent in client.StreamAsync(Command(), TestContext.Current.CancellationToken))
            events.Add(providerEvent);

        var failure = Assert.IsType<ChatProviderEvent.Failure>(events[^1]);
        Assert.Equal(CostBasis.Estimated, failure.Attempt.Basis);
        Assert.Equal(1, failure.Attempt.OutputUnits);
    }

    [Fact]
    public async Task ChatUsageWithTotalBelowKnownComponentFallsBackToEstimation()
    {
        const string payload = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"completion_tokens\":10,\"total_tokens\":2}}\n\n" +
            "data: [DONE]\n\n";
        var handler = new ResponseHandler(payload);
        var client = new OpenAiChatClient(new SingleClientRegistry(new HttpClient(handler)), new ChatProviderOptions(),
            Catalog(), PricedPolicy(), new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var events = new List<ChatProviderEvent>();

        await foreach (var providerEvent in client.StreamAsync(Command(), TestContext.Current.CancellationToken))
            events.Add(providerEvent);

        var failure = Assert.IsType<ChatProviderEvent.Failure>(events[^1]);
        Assert.Equal("truncated_stream", failure.Category);
        Assert.Equal(CostBasis.Estimated, failure.Attempt.Basis);
    }

    [Fact]
    public async Task ChatServerErrorBillsTheTransmittedPromptAsAnEstimate()
    {
        var handler = new ResponseHandler("error", "text/plain", HttpStatusCode.InternalServerError);
        var policy = PricedPolicy();
        var client = new OpenAiChatClient(new SingleClientRegistry(new HttpClient(handler)), new ChatProviderOptions(),
            Catalog(), policy, new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var events = new List<ChatProviderEvent>();

        await foreach (var providerEvent in client.StreamAsync(Command(), TestContext.Current.CancellationToken))
            events.Add(providerEvent);

        var failure = Assert.IsType<ChatProviderEvent.Failure>(Assert.Single(events));
        Assert.Equal("provider_http_500", failure.Category);
        Assert.True(failure.Retryable);
        Assert.Equal(CostBasis.Estimated, failure.Attempt.Basis);
        Assert.True(failure.Attempt.InputUnits > 0);
        Assert.Equal(0, failure.Attempt.OutputUnits);
        Assert.Equal(policy.Get(Resources.QwenFlash).Price.Calculate(failure.Attempt.InputUnits, 0), failure.Attempt.Cost);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ChatRejectedRequestsBillExactZeroCost(HttpStatusCode status)
    {
        var handler = new ResponseHandler("error", "text/plain", status);
        var client = new OpenAiChatClient(new SingleClientRegistry(new HttpClient(handler)), new ChatProviderOptions(),
            Catalog(), PricedPolicy(), new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var events = new List<ChatProviderEvent>();

        await foreach (var providerEvent in client.StreamAsync(Command(), TestContext.Current.CancellationToken))
            events.Add(providerEvent);

        var failure = Assert.IsType<ChatProviderEvent.Failure>(Assert.Single(events));
        Assert.Equal($"provider_http_{(int)status}", failure.Category);
        Assert.Equal(CostBasis.Exact, failure.Attempt.Basis);
        Assert.True(failure.Attempt.CostKnown);
        Assert.Equal(0, failure.Attempt.InputUnits);
        Assert.Equal(NanoYuan.Zero, failure.Attempt.Cost);
    }

    [Fact]
    public async Task ContradictoryChatUsageTotalsAreNormalizedInsteadOfRejected()
    {
        const string payload = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":999}}\n\n" +
            "data: [DONE]\n\n";
        var handler = new ResponseHandler(payload);
        var client = new OpenAiChatClient(new SingleClientRegistry(new HttpClient(handler)), new ChatProviderOptions(),
            Catalog(), ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var events = new List<ChatProviderEvent>();

        await foreach (var providerEvent in client.StreamAsync(Command(), TestContext.Current.CancellationToken))
            events.Add(providerEvent);

        var terminal = Assert.IsType<ChatProviderEvent.Terminal>(events[^1]);
        Assert.Equal(new ChatUsage(10, 2, 12), terminal.Usage);
        Assert.Equal(CostBasis.Exact, terminal.Attempt.Basis);
        Assert.Equal(10, terminal.Attempt.InputUnits);
        Assert.Equal(2, terminal.Attempt.OutputUnits);
    }

    [Fact]
    public async Task MissingChatUsageComponentsAreDerivedAndReasoningClamped()
    {
        const string payload = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"total_tokens\":9}}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"completion_tokens\":4,\"total_tokens\":9," +
            "\"completion_tokens_details\":{\"reasoning_tokens\":50}}}\n\n" +
            "data: [DONE]\n\n";
        var handler = new ResponseHandler(payload);
        var client = new OpenAiChatClient(new SingleClientRegistry(new HttpClient(handler)), new ChatProviderOptions(),
            Catalog(), ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var events = new List<ChatProviderEvent>();

        await foreach (var providerEvent in client.StreamAsync(Command(), TestContext.Current.CancellationToken))
            events.Add(providerEvent);

        var terminal = Assert.IsType<ChatProviderEvent.Terminal>(events[^1]);
        Assert.Equal(new ChatUsage(5, 4, 9, 4), terminal.Usage);
    }

    [Fact]
    public async Task TranslationServerErrorBillsExactInputCharactersAsAnEstimate()
    {
        var policy = PricedPolicy(new(100), new(100));
        var client = new OpenAiTranslationClient(
            new SingleClientRegistry(new HttpClient(new ResponseHandler("failure", "text/plain", HttpStatusCode.ServiceUnavailable))),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, Catalog(), policy,
            new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("provider_http_503", result.Outcome);
        Assert.Equal(CostBasis.Estimated, result.Attempt.Basis);
        Assert.True(result.Attempt.InputUnits > 0);
        Assert.Equal(0, result.Attempt.OutputUnits);
        Assert.Equal(policy.Get(Resources.Translation).Price.Calculate(result.Attempt.InputUnits, 0), result.Attempt.Cost);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task TranslationRejectedRequestsBillExactZeroCost(HttpStatusCode status)
    {
        var client = new OpenAiTranslationClient(
            new SingleClientRegistry(new HttpClient(new ResponseHandler("failure", "text/plain", status))),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, Catalog(), PricedPolicy(),
            new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(CostBasis.Exact, result.Attempt.Basis);
        Assert.Equal(0, result.Attempt.InputUnits);
        Assert.Equal(NanoYuan.Zero, result.Attempt.Cost);
    }

    [Fact]
    public async Task TranslationUnparseableResponsesBillEstimatedInputOnly()
    {
        var policy = PricedPolicy(new(100), new(100));
        var client = new OpenAiTranslationClient(
            new SingleClientRegistry(new HttpClient(new ResponseHandler("not json", "application/json"))),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, Catalog(), policy,
            new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(TranslationCommand(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("invalid_response", result.Outcome);
        Assert.Equal(CostBasis.Estimated, result.Attempt.Basis);
        Assert.Equal(AttemptDispatchState.Dispatched, result.Attempt.DispatchState);
        Assert.Equal(policy.Get(Resources.Translation).Price.Calculate(result.Attempt.InputUnits, 0), result.Attempt.Cost);
    }

    [Fact]
    public async Task TranslationTimeoutsRemainUnknownCost()
    {
        var client = new OpenAiTranslationClient(
            new SingleClientRegistry(new HttpClient(new DelayingHandler())),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, Catalog(), PricedPolicy(),
            new DependencyHealth(TimeProvider.System), TimeProvider.System);

        var result = await client.TranslateAsync(
            TranslationCommand() with { Timeout = TimeSpan.FromMilliseconds(20) }, CancellationToken.None);

        Assert.Equal("attempt_timeout", result.Outcome);
        Assert.Equal(CostBasis.Unknown, result.Attempt.Basis);
        Assert.False(result.Attempt.CostKnown);
        Assert.Equal(NanoYuan.Zero, result.Attempt.Cost);
    }

    private static ServicePolicy PricedPolicy(NanoYuan? input = null, NanoYuan? output = null, EstimationRatios? ratios = null)
    {
        var defaults = ServicePolicy.Defaults();
        var price = new UnitPrice(input ?? new(200), output ?? new(800));
        var resources = defaults.ResourcePolicies.Select(value => value with { Price = price }).ToArray();
        return new(defaults.Revision, resources, defaults.PrincipalDailyAllowance, defaults.DailyOperatorBudget,
            defaults.MonthlyOperatorBudget, defaults.ActiveLeaseTtl, defaults.LeaseRenewalInterval, estimation: ratios);
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
    public async Task ChatMergesSystemMessagesIntoTheUserTurnForConfiguredModelsOnly()
    {
        const string response = "data: {\"id\":\"one\",\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}\n\n" +
            "data: [DONE]\n\n";

        var merged = await ForwardedMessagesAsync(new ResponseHandler(response), Resources.QwenMtFlash, """
            {"model":"qwen-mt-flash","messages":[
                {"role":"system","content":"first"},
                {"role":"system","content":"second"},
                {"role":"user","content":"hello","name":"caller"},
                {"role":"assistant","content":"hola"}]}
            """u8.ToArray());
        Assert.Equal(2, merged.GetArrayLength());
        Assert.Equal("user", merged[0].GetProperty("role").GetString());
        Assert.Equal("first\n\nsecond\n\nhello", merged[0].GetProperty("content").GetString());
        Assert.Equal("caller", merged[0].GetProperty("name").GetString());
        Assert.Equal("assistant", merged[1].GetProperty("role").GetString());
        Assert.Equal("hola", merged[1].GetProperty("content").GetString());

        var withoutUserTurn = await ForwardedMessagesAsync(new ResponseHandler(response), Resources.QwenMtFlash, """
            {"model":"qwen-mt-flash","messages":[
                {"role":"system","content":"instructions"},
                {"role":"assistant","content":"hola"}]}
            """u8.ToArray());
        Assert.Equal(2, withoutUserTurn.GetArrayLength());
        Assert.Equal("user", withoutUserTurn[0].GetProperty("role").GetString());
        Assert.Equal("instructions", withoutUserTurn[0].GetProperty("content").GetString());
        Assert.Equal("assistant", withoutUserTurn[1].GetProperty("role").GetString());

        var verbatim = await ForwardedMessagesAsync(new ResponseHandler(response), Resources.QwenFlash, """
            {"model":"qwen3.8-flash","messages":[
                {"role":"system","content":"instructions"},
                {"role":"user","content":"hello"}]}
            """u8.ToArray());
        Assert.Equal(2, verbatim.GetArrayLength());
        Assert.Equal("system", verbatim[0].GetProperty("role").GetString());
        Assert.Equal("instructions", verbatim[0].GetProperty("content").GetString());
        Assert.Equal("user", verbatim[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task TranslationDesignationDoesNotForceSystemMergeWithoutTheModelFlag()
    {
        const string response = "data: {\"id\":\"one\",\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}\n\n" +
            "data: [DONE]\n\n";
        ProviderModelOptions Model(string upstream, bool mergesSystemIntoUser = false) => new()
        {
            MergesSystemIntoUser = mergesSystemIntoUser,
            Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
            {
                ["test"] = new() { Provider = "test", UpstreamModel = upstream, MaxConcurrentRequests = 16 },
            },
        };
        var catalog = new ProviderModelCatalog(new ProviderModelsOptions
        {
            CloudProviders = new Dictionary<string, CloudProviderOptions>(StringComparer.Ordinal)
            {
                ["test"] = new() { Endpoint = "https://provider.test/chat", ApiKey = "test" },
            },
            Models = new Dictionary<string, ProviderModelOptions>(StringComparer.Ordinal)
            {
                [Resources.QwenFlash] = Model("test-model"),
                [Resources.QwenMtFlash] = Model("test-mt", mergesSystemIntoUser: true),
            },
        }, new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash, Resources.QwenMtFlash] }, requireHttps: true);

        Assert.True(catalog.IsTranslationModel(Resources.QwenFlash));
        Assert.False(catalog.MergesSystemIntoUser(Resources.QwenFlash));
        Assert.True(catalog.MergesSystemIntoUser(Resources.QwenMtFlash));

        var verbatim = await ForwardedMessagesAsync(new ResponseHandler(response), Resources.QwenFlash, """
            {"model":"qwen3.8-flash","messages":[
                {"role":"system","content":"instructions"},
                {"role":"user","content":"hello"}]}
            """u8.ToArray(), catalog);
        Assert.Equal(2, verbatim.GetArrayLength());
        Assert.Equal("system", verbatim[0].GetProperty("role").GetString());
        Assert.Equal("instructions", verbatim[0].GetProperty("content").GetString());
        Assert.Equal("user", verbatim[1].GetProperty("role").GetString());
    }

    private static async Task<JsonElement> ForwardedMessagesAsync(ResponseHandler handler, string model, byte[] payload,
        ProviderModelCatalog? catalog = null)
    {
        var client = new OpenAiChatClient(new SingleClientRegistry(new HttpClient(handler)), new ChatProviderOptions(),
            catalog ?? Catalog(), ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var command = Command() with
        {
            Request = new ChatCommand(model, payload),
            Access = new ProviderAccessSelection(model, "test", "test", "test-model"),
        };
        var events = new List<ChatProviderEvent>();
        await foreach (var providerEvent in client.StreamAsync(command, TestContext.Current.CancellationToken))
            events.Add(providerEvent);
        Assert.IsType<ChatProviderEvent.Terminal>(events[^1]);
        using var forwarded = JsonDocument.Parse(handler.RequestBody!);
        return forwarded.RootElement.GetProperty("messages").Clone();
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

    private static TranslationProviderCommand NativeTranslationCommand()
    {
        var command = TranslationCommand();
        return command with
        {
            Access = new ProviderAccessSelection(Resources.QwenMtFlash, "test", "test", "test-mt"),
        };
    }

    private static OpenAiTranslationClient TranslationClient(ResponseHandler handler, TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        return new(new SingleClientRegistry(new HttpClient(handler)),
            new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, Catalog(),
            ServicePolicy.Defaults(), new DependencyHealth(time), time);
    }

    private static OpenAiTranslationClient TranslationClient(IProviderHttpClientRegistry registry)
    {
        return new(registry, new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, Catalog(),
            ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);
    }

    private static ProviderModelCatalog Catalog(bool? translationEnableThinking = null)
    {
        ProviderModelOptions Model(string upstream, bool mergesSystemIntoUser = false, bool nativeTranslationOptions = false) => new()
        {
            MergesSystemIntoUser = mergesSystemIntoUser,
            NativeTranslationOptions = nativeTranslationOptions,
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
                [Resources.QwenVisionFlash] = Model("test-vision"),
                [Resources.QwenMtFlash] = Model("test-mt", mergesSystemIntoUser: true, nativeTranslationOptions: true),
            },
        }, new TranslationProviderOptions { LogicalModels = [Resources.QwenMtFlash] }, requireHttps: true);
    }

    private sealed class SingleClientRegistry(HttpClient client) : IProviderHttpClientRegistry
    {
        public List<ProviderCircuitOutcome> Outcomes { get; } = [];
        public HttpClient CreateClient(ProviderAccessSelection selection) => client;
        public ValueTask ReportAsync(ProviderAccessSelection selection, ProviderCircuitOutcome outcome,
            TimeSpan? retryAfter = null)
        {
            Outcomes.Add(outcome);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
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
