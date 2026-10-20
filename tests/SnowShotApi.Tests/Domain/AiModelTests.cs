using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Providers;
using SnowShot.Infrastructure.Telemetry;
using SnowShotApi.Tests.Api;

namespace SnowShotApi.Tests.Domain;

public sealed class AiModelTests
{
    internal static (ProviderModelCatalog Catalog, ServicePolicy Policy) Configuration()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Providers:CloudProviders:aliyun:ApiKey"] = "test" }).Build();
        var catalog = new ProviderModelCatalog(configuration.GetSection("Providers").Get<ProviderModelsOptions>()!,
            configuration.GetSection("Providers:Translation").Get<TranslationProviderOptions>()!, true);
        var policy = configuration.GetSection("Policy").Get<PolicyOptions>()!
            .Build(catalog.Models.Select(model => model.Model), catalog.RoutingIdentity, publishedModelsOnly: true);
        return (catalog, policy);
    }

    [Fact]
    public void ProductionCatalogPublishesOnlyAiModelAndRejectsDirectTextModel()
    {
        var (catalog, policy) = Configuration();
        var model = Assert.Single(catalog.Models);
        Assert.Equal(Resources.AiModel, model.Model);
        Assert.True(model.SupportsVision);
        Assert.True(model.SupportsReasoning);
        Assert.False(model.Translation);
        Assert.Equal("default", model.TranslationMode);
        Assert.False(catalog.Contains(Resources.QwenTextFlash));
        Assert.Equal([Resources.AiModel, Resources.LatexExtraction, Resources.TableExtraction, Resources.Translation],
            policy.ResourcePolicies.Select(value => value.Resource).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(15, policy.Revision);
        Assert.Equal(60, policy.Get(Resources.AiModel).Admission.RequestsPerMinute);
        Assert.Equal(4, policy.Get(Resources.AiModel).Admission.PerPrincipalConcurrency);
        Assert.Equal(64, policy.Get(Resources.AiModel).Admission.GlobalConcurrency);
        Assert.Equal(128, policy.Get(Resources.AiModel).Admission.GlobalQueueLength);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Get(Resources.AiModel).Admission.QueueWait);
        Assert.Equal(TimeSpan.FromSeconds(300), policy.Get(Resources.AiModel).ExecutionDeadline);
        Assert.Equal(3_000_000_000, policy.PrincipalDailyAllowance.Value);
    }

    [Theory]
    [InlineData("ai-model")]
    [InlineData("qwen3.8-flash")]
    [InlineData("qwen-mt-flash")]
    public void EveryPublicIdentityRoutesByActualImageParts(string id)
    {
        var (catalog, _) = Configuration();
        foreach (var content in new[] { "\"the word image_url\"", "[{\"type\":\"text\",\"text\":\"image\"}]" })
        {
            var command = Resolve(catalog, id, $$"""[{"role":"user","content":{{content}}}]""");
            Assert.Equal(Resources.AiModel, command.Model);
            Assert.Equal(id, command.RequestedModel);
            Assert.Equal(Resources.QwenTextFlash, command.ExecutionModel);
        }
        foreach (var url in new[] { "https://example.test/image.png", "data:image/webp;base64,UklGRg==" })
        {
            var command = Resolve(catalog, id, $$$"""
                [{"role":"user","content":[{"type":"image_url","image_url":{"url":"{{{url}}}"}}]},
                 {"role":"assistant","content":"Seen."},{"role":"user","content":"Describe it again."}]
                """);
            Assert.Equal(Resources.QwenFlash, command.ExecutionModel);
            using var payload = JsonDocument.Parse(command.Utf8Json);
            Assert.False(payload.RootElement.GetProperty("enable_thinking").GetBoolean());
            Assert.Equal(url, payload.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString());
        }
    }

    [Theory]
    [InlineData("""{"type":"image_url"}""")]
    [InlineData("""{"type":"image_url","image_url":"https://example.test/image.png"}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":""}}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":"file:///image.png"}}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":"data:image/png;base64,"}}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":"data:image/png;base64,not-base64"}}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":"data:image/;base64,YQ=="}}""")]
    [InlineData("""{"type":"text","image_url":{"url":"https://example.test/image.png"}}""")]
    public async Task MalformedImagesAreRejectedBeforeReservation(string part)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        using var body = new StringContent($$"""{"model":"ai-model","messages":[{"role":"user","content":[{{part}}]}]}""",
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/v1/chat/completions", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Ledger.Reservations);
        Assert.Empty(factory.ProviderAccess.Requests);
    }

    [Fact]
    public void LegacyTranslationSettingsBecomeInstructionsWithoutLosingSourceOrOptions()
    {
        var (catalog, _) = Configuration();
        var command = ChatRequestResolver.Resolve(new(Resources.QwenMtFlash, Encoding.UTF8.GetBytes("""
            {"model":"qwen-mt-flash","messages":[{"role":"user","content":"Translate me."}],
             "translation_options":{"source_lang":"auto","target_lang":"Traditional Chinese","domains":"IT",
               "terms":[{"source":"API","target":"API"}],"tm_list":[{"source":"hello","target":"您好"}]},
             "incremental_output":true,"enable_thinking":true,"vendor_option":7}
            """)), catalog);
        using var document = JsonDocument.Parse(command.Utf8Json);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("translation_options", out _));
        Assert.False(root.TryGetProperty("incremental_output", out _));
        Assert.True(root.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(7, root.GetProperty("vendor_option").GetInt32());
        var messages = root.GetProperty("messages");
        Assert.Equal("Translate me.", messages[1].GetProperty("content").GetString());
        var instruction = messages[0].GetProperty("content").GetString()!;
        Assert.Contains("Traditional Chinese", instruction, StringComparison.Ordinal);
        Assert.Contains("\"tm_list\"", instruction, StringComparison.Ordinal);
        Assert.Contains("\"terms\"", instruction, StringComparison.Ordinal);
        Assert.Contains("\"domains\":\"IT\"", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AliasesShareReservationAndDuplicateRequestIdentity()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Request-ID", "same-request");
        foreach (var id in new[] { Resources.AiModel, Resources.QwenFlash, Resources.QwenMtFlash })
        {
            using var response = await client.PostAsJsonAsync("/api/v1/chat/completions",
                new { model = id, messages = new[] { new { role = "user", content = "Hello." } } },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var reservations = factory.Ledger.Reservations.ToArray();
        Assert.Equal(3, reservations.Length);
        foreach (var reservation in reservations)
        {
            Assert.Equal(Resources.AiModel, reservation.Snapshot.Resource);
            Assert.Equal(Resources.QwenTextFlash, reservation.Snapshot.ModelPricing!.ExecutionModel);
            Assert.Equal(reservations[0].IdempotencyHash, reservation.IdempotencyHash);
        }
        Assert.All(factory.ProviderAccess.Requests, request => Assert.Equal(Resources.QwenTextFlash, request.LogicalModel));
        Assert.All(factory.Admission.Requests, request => Assert.Equal(Resources.AiModel, request.Resource));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatRetriesKeepTheReservedRouteAndOnlySuccessfulUsageIsDeducted(bool images)
    {
        await using var factory = new ApiFactory();
        factory.Chat.FailFirstAttempt = true;
        using var client = factory.CreateAnonymousClient();
        var content = images
            ? """[{"type":"image_url","image_url":{"url":"https://example.test/a.png"}}]"""
            : "\"Hello\"";
        using var body = new StringContent($$$"""{"model":"qwen-mt-flash","messages":[{"role":"user","content":{{{content}}}}]}""",
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/v1/chat/completions", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var route = images ? Resources.QwenFlash : Resources.QwenTextFlash;
        Assert.Equal(2, factory.Chat.Commands.Count);
        Assert.All(factory.Chat.Commands, command =>
        {
            Assert.Equal(route, command.Access.LogicalModel);
            Assert.Equal(route, command.Operation.Snapshot.ModelPricing!.ExecutionModel);
        });
        Assert.Equal(2, factory.Ledger.Attempts.Count);
        Assert.True(factory.Ledger.Attempts.First().Cost.Value > 0);
        Assert.Equal(factory.Policy.ModelPricing[route].Calculate(100, 20),
            Assert.Single(factory.Ledger.Settlements).ReportedPublicCost);
    }

    [Theory]
    [InlineData(32_000, 200, 800, 40)]
    [InlineData(32_001, 600, 2400, 120)]
    [InlineData(256_000, 600, 2400, 120)]
    [InlineData(256_001, 1200, 4800, 240)]
    public void EntireRequestUsesItsPromptTier(long input, long inputRate, long outputRate, long cachedRate)
    {
        var tariff = Configuration().Policy.ModelPricing[Resources.QwenTextFlash];
        Assert.Equal(input * inputRate + 100 * outputRate, tariff.Calculate(input, 100).Value);
        Assert.Equal((input - 500) * inputRate + 500 * cachedRate + 100 * outputRate, tariff.Calculate(input, 100, 500).Value);
    }

    [Fact]
    public void TariffsRejectInvalidThresholdsRatesAndUsage()
    {
        Assert.Throws<PolicyValidationException>(() => new TokenTariff([]));
        Assert.Throws<PolicyValidationException>(() => new TokenTariff([new(32000, 200, 800, 201)]));
        Assert.Throws<PolicyValidationException>(() => new TokenTariff([new(32000, 200, 800, 40), new(32000, 600, 2400, 120)]));
        Assert.Throws<PolicyValidationException>(() => new TokenTariff([new(1000000, long.MaxValue, 1, 0)]));
        var tariff = Configuration().Policy.ModelPricing[Resources.QwenTextFlash];
        Assert.Throws<ArgumentOutOfRangeException>(() => tariff.Calculate(10, 1, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => tariff.Calculate(-1, 1));
    }

    [Fact]
    public void TariffAndRoutingChangesAffectPolicyFingerprint()
    {
        var (_, policy) = Configuration();
        ServicePolicy Copy(IReadOnlyDictionary<string, TokenTariff> prices, string routing) =>
            new(policy.Revision, policy.ResourcePolicies, policy.PrincipalDailyAllowance, policy.DailyOperatorBudget,
                policy.MonthlyOperatorBudget, policy.ActiveLeaseTtl, policy.LeaseRenewalInterval,
                estimation: policy.Estimation, modelPricing: prices, routingIdentity: routing);
        var changed = policy.ModelPricing.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        changed[Resources.QwenTextFlash] = new([new(1_000_000, 201, 800, 40)]);
        Assert.NotEqual(policy.Fingerprint, Copy(changed, policy.RoutingIdentity).Fingerprint);
        Assert.NotEqual(policy.Fingerprint, Copy(policy.ModelPricing, "different-route").Fingerprint);
        var snapshot = policy.PricingFor(Resources.QwenTextFlash)!;
        var restored = JsonSerializer.Deserialize<ModelPricingSnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal(snapshot.ExecutionModel, restored.ExecutionModel);
        Assert.Equal(snapshot.Tariff.CanonicalDocument, restored.Tariff.CanonicalDocument);
    }

    [Fact]
    public async Task VisionCircuitFailureDoesNotBlockTheTextPool()
    {
        var (catalog, _) = Configuration();
        Assert.Equal([Resources.QwenTextFlash, Resources.QwenFlash],
            catalog.ChatExecutionModels.Order(StringComparer.Ordinal).ToArray());
        var circuits = new InMemoryProviderCircuitRegistry(new() { ConsecutiveFailuresToOpen = 2 }, TimeProvider.System);
        var vision = catalog.Selections(Resources.QwenFlash)[0];
        await circuits.ReportAsync(vision, ProviderCircuitOutcome.TransientFailure, null, TestContext.Current.CancellationToken);
        await circuits.ReportAsync(vision, ProviderCircuitOutcome.TransientFailure, null, TestContext.Current.CancellationToken);
        var pool = new InMemoryProviderAccessPool(catalog, circuits);
        ProviderAccessRequest Request(string model) => new(model, new HashSet<string>(), TimeSpan.Zero,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        await using var failed = await pool.AcquireAsync(Request(Resources.QwenFlash), TestContext.Current.CancellationToken);
        Assert.False(failed.Acquired);
        await using var text = await pool.AcquireAsync(Request(Resources.QwenTextFlash), TestContext.Current.CancellationToken);
        Assert.True(text.Acquired);
        Assert.Equal(Resources.QwenTextFlash, text.Selection!.LogicalModel);
    }

    [Theory]
    [InlineData("""{"prompt_tokens":10,"completion_tokens":3,"prompt_tokens_details":{"cached_tokens":11}}""")]
    [InlineData("""{"prompt_tokens":"ten","completion_tokens":3}""")]
    [InlineData("""{"prompt_tokens":10,"completion_tokens":3,"prompt_tokens_details":{"cached_tokens":"5"}}""")]
    [InlineData("""{"total_tokens":10}""")]
    [InlineData("""{"prompt_tokens":1000000001,"completion_tokens":0}""")]
    public void UnusableProviderUsageCannotClaimExactTokenAccounting(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(OpenAiUsage.ReadDetails(document.RootElement));
    }

    [Fact]
    public void MixedMultipleImagesKeepTheVisionRouteAndOrdinaryConversation()
    {
        var (catalog, _) = Configuration();
        var messages = """[{"role":"system","content":"Inspect"},{"role":"user","content":[{"type":"text","text":"Compare"},{"type":"image_url","image_url":{"url":"https://example.test/a.png"}},{"type":"image_url","image_url":{"url":"data:image/png;base64,YQ=="}}]}]""";
        var resolved = Resolve(catalog, Resources.QwenMtFlash, messages);
        Assert.Equal(Resources.QwenFlash, resolved.ExecutionModel);
        using var before = JsonDocument.Parse(messages);
        using var after = JsonDocument.Parse(resolved.Utf8Json);
        Assert.True(JsonElement.DeepEquals(before.RootElement, after.RootElement.GetProperty("messages")));
    }

    [Fact]
    public async Task ChatForwardsResolvedModelRewritesResponseAndPricesCachedUsage()
    {
        var (catalog, policy) = Configuration();
        var resolved = Resolve(catalog, Resources.QwenMtFlash, """[{"role":"user","content":"hello"}]""");
        using var handler = new ResponseHandler("""
            data: {"model":"qwen3.7-flash","choices":[{"delta":{"content":"hello"}}]}

            data: {"model":"qwen3.7-flash","choices":[],"usage":{"prompt_tokens":32001,"completion_tokens":100,"total_tokens":32101,"prompt_tokens_details":{"cached_tokens":500},"completion_tokens_details":{"reasoning_tokens":40}}}

            data: [DONE]

            """);
        using var registry = new Registry(handler);
        var health = new DependencyHealth(TimeProvider.System);
        health.Report("chat_provider", false);
        var provider = new OpenAiChatClient(registry, new(), catalog, policy, health, TimeProvider.System);
        var access = catalog.Selections(resolved.ExecutionModel)[0];
        var events = new List<ChatProviderEvent>();
        await foreach (var value in provider.StreamAsync(new(Handle(policy, resolved.ExecutionModel), resolved, access,
            "request", "trace", 1, Guid.CreateVersion7(), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken))
            events.Add(value);
        using var sent = JsonDocument.Parse(handler.Payload!);
        Assert.Equal(Resources.QwenTextFlash, sent.RootElement.GetProperty("model").GetString());
        Assert.True(sent.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        foreach (var frame in events.OfType<ChatProviderEvent.Frame>())
        {
            using var json = JsonDocument.Parse(frame.Utf8Json);
            Assert.Equal(Resources.QwenMtFlash, json.RootElement.GetProperty("model").GetString());
        }
        var terminal = Assert.Single(events.OfType<ChatProviderEvent.Terminal>());
        Assert.Equal(40, terminal.Usage!.ReasoningTokens);
        Assert.Equal(500, terminal.Usage.CachedInputTokens);
        Assert.Equal((32_001 - 500) * 600L + 500 * 120 + 100 * 2400, terminal.Attempt.Cost.Value);
        Assert.Equal(CostBasis.Exact, terminal.Attempt.Basis);
        Assert.False(health.Snapshot()["chat_provider"].Healthy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TranslationKeepsPublicCharactersAndUsesProviderTokensOrEstimates(bool withUsage)
    {
        var (catalog, policy) = Configuration();
        var response = """{"choices":[{"message":{"content":"{\"translations\":[{\"index\":0,\"content\":\"您好\"}]}"}}]""" +
            (withUsage ? ""","usage":{"prompt_tokens":50,"completion_tokens":10,"total_tokens":60}}""" : "}");
        using var handler = new ResponseHandler(response);
        using var registry = new Registry(handler);
        var provider = new OpenAiTranslationClient(registry, new(), catalog, policy,
            new DependencyHealth(TimeProvider.System), TimeProvider.System);
        var result = await provider.TranslateAsync(new("hello", "en", "zh-CHS", "general",
            catalog.Selections(Resources.QwenTextFlash)[0], Handle(policy, Resources.QwenTextFlash), "request", "trace",
            1, 0, 1, 1, Guid.CreateVersion7(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal("您好", Assert.Single(result.Results));
        Assert.True(result.PublicInputCharacters > 5);
        Assert.True(result.PublicOutputCharacters > 2);
        Assert.Equal(withUsage ? CostBasis.Exact : CostBasis.Estimated, result.Basis);
        if (withUsage)
        {
            Assert.Equal(50, result.OperatorInputUnits);
            Assert.Equal(10, result.OperatorOutputUnits);
            Assert.Equal(18_000, result.Attempt.Cost.Value);
        }
        else
        {
            Assert.Equal(policy.Estimation.EstimateInputTokens(handler.Payload!.Length), result.OperatorInputUnits);
            Assert.True(result.Attempt.Cost.Value > 0);
        }
        using var payload = JsonDocument.Parse(handler.Payload!);
        Assert.Equal(Resources.QwenTextFlash, payload.RootElement.GetProperty("model").GetString());
        Assert.False(payload.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("json_object", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    private static ChatCommand Resolve(ProviderModelCatalog catalog, string id, string messages) =>
        ChatRequestResolver.Resolve(new(id, Encoding.UTF8.GetBytes($$"""{"model":"{{id}}","messages":{{messages}}}""")), catalog);

    private static OperationHandle Handle(ServicePolicy policy, string model) =>
        new(Guid.CreateVersion7(), new byte[32], 1, DateTimeOffset.UtcNow.AddMinutes(1),
            new(policy.Revision, policy.Fingerprint, Resources.AiModel, policy.Get(Resources.AiModel).Price,
                policy.PrincipalDailyAllowance, new(1), new(30_000_000), policy.PricingFor(model)));

    private sealed class ResponseHandler(string response) : HttpMessageHandler
    {
        public byte[]? Payload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    private sealed class Registry(ResponseHandler handler) : IProviderHttpClientRegistry, IDisposable
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);
        public HttpClient CreateClient(ProviderAccessSelection selection) => _client;
        public ValueTask ReportAsync(ProviderAccessSelection selection, ProviderCircuitOutcome outcome, TimeSpan? retryAfter = null) =>
            ValueTask.CompletedTask;
        public void Dispose() => _client.Dispose();
    }
}
