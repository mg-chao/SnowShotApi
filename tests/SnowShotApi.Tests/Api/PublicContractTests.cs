using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SnowShot.Application;
using SnowShot.Contracts;
using SnowShot.Domain;

namespace SnowShotApi.Tests.Api;

public sealed class PublicContractTests
{
    private const int MaximumTableImageBytes = 800 * 1024;

    [Fact]
    public async Task EnglishIsTheDefaultResponseLanguage()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();

        using var response = await client.GetAsync("/api/v2/translation/types", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("en-US", Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Equal("Request success", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("AI Translation", document.RootElement.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task UnsupportedLanguageFallsBackToEnglish()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR");

        using var response = await client.GetAsync("/api/v1/chat/models", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("en-US", Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Equal("Request success", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("Qwen Flash", document.RootElement.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AcceptLanguageQualitySelectsThePreferredSupportedLanguage()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN;q=0.5, en-US;q=0.9");

        using var response = await client.GetAsync("/api/v2/translation/types", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("en-US", Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Equal("Request success", document.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("zh-Hans")]
    public async Task ChineseLocalizesSuccessEnvelopeAndAdvertisesSelectedLanguage(string language)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);

        using var response = await client.GetAsync("/api/v2/translation/types", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("zh-CN", Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Contains("Accept-Language", response.Headers.Vary);
        Assert.Equal("请求成功", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("AI 翻译", document.RootElement.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChineseLocalizesProblemTitleAndDetailedTranslationValidation()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");

        using var response = await client.PostAsJsonAsync("/api/v2/translation/translate", new
        {
            type = 0,
            content = Array.Empty<string?>(),
            from = "invalid",
            to = "invalid",
            domain = "invalid",
        }, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var problem = document.RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("zh-CN", Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Equal("错误请求", problem.GetProperty("title").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("content 必须包含 1 到 50 项", detail, StringComparison.Ordinal);
        Assert.Contains("不支持的语言代码：invalid", detail, StringComparison.Ordinal);
        Assert.Contains("不支持的领域：invalid", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChineseLocalizesModelAndRequestIdValidation()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");

        using var modelResponse = await client.PostAsJsonAsync("/api/v1/chat/completions",
            new { model = "unknown", messages = new[] { new { role = "user", content = "hello" } } },
            TestContext.Current.CancellationToken);
        using var modelDocument = JsonDocument.Parse(await modelResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("模型 `unknown` 不存在，或您无权访问该模型。", modelDocument.RootElement.GetProperty("detail").GetString());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/translation/translate")
        {
            Content = JsonContent.Create(new { type = 0, content = new[] { "one" }, from = "en", to = "zh-CHS", domain = "general" }),
        };
        request.Headers.TryAddWithoutValidation("X-Request-ID", "unsafe request id");
        using var idResponse = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var idDocument = JsonDocument.Parse(await idResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("X-Request-ID 必须且只能包含一个值，且最多为 64 个可见 ASCII 字符。",
            idDocument.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ModelListPreservesShapeIdentifiersAndEnglishLocalization()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        using var response = await client.GetAsync("/api/v1/chat/models", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());
        var models = document.RootElement.GetProperty("data");
        Assert.Equal(["qwen-flash", "qwen-plus", "qwen3-vl-flash", "deepseek-v4-flash"],
            models.EnumerateArray().Select(value => value.GetProperty("model").GetString()!).ToArray());
        Assert.Equal(["Qwen Flash", "Qwen Plus", "Qwen VL Flash", "DeepSeek V4 Flash"],
            models.EnumerateArray().Select(value => value.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task ChineseModelNamesUseUnifiedPublicLocalization()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");

        using var response = await client.GetAsync("/api/v1/chat/models", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["通义千问 Flash", "通义千问 Plus", "通义千问 VL Flash", "DeepSeek V4 Flash"],
            document.RootElement.GetProperty("data").EnumerateArray()
                .Select(value => value.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task ChineseLocalizationIsValidUnicode()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");
        using var response = await client.GetAsync("/api/v2/translation/types", TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("AI 翻译", json.RootElement.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task TranslationPreservesOrderingAndSnapshotsPolicy()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/translation/translate")
        {
            Content = JsonContent.Create(new { type = 0, content = new[] { "one", "two" }, from = "en", to = "zh-CHS", domain = "general" }),
        };
        request.Headers.Add("X-Request-ID", new string('r', 64));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["translated:one", "translated:two"], json.RootElement.GetProperty("data").GetProperty("results")
            .EnumerateArray().Select(value => value.GetProperty("content").GetString()!).ToArray());
        var reservation = Assert.Single(factory.Ledger.Reservations);
        Assert.Equal(32, reservation.IdempotencyHash.Length);
        Assert.Equal(factory.Policy.Fingerprint, reservation.Snapshot.PolicyFingerprint);
        Assert.Equal(1, reservation.Snapshot.PublicReservation.Value);
        Assert.Equal(360_000, Assert.Single(factory.Ledger.Settlements).ReportedPublicCost.Value);
        Assert.Equal(2, factory.Ledger.Preparations.Count);
        Assert.Equal(2, factory.Ledger.Attempts.Count);
        var routedModel = Assert.Single(factory.Translation.Commands.Select(command => command.Access.LogicalModel).Distinct());
        Assert.Contains(routedModel, new[] { Resources.DeepSeekV4, Resources.QwenPlus });
        Assert.All(factory.Ledger.Attempts, attempt => Assert.Equal($"{routedModel}/test/aliyun", attempt.Provider));
    }

    [Fact]
    public async Task TranslationRunsAtMostFourIndependentConversationsAndPreservesOrdering()
    {
        await using var factory = new ApiFactory();
        factory.Translation.Handler = async (command, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds((8 - command.ItemIndex) * 20), cancellationToken);
            return FakeTranslationClient.Result(command, value: $"result-{command.ItemIndex}");
        };
        using var client = factory.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync("/api/v2/translation/translate", new
        {
            type = 0,
            content = Enumerable.Range(0, 8).Select(index => $"item-{index}").ToArray(),
            from = "en",
            to = "zh-CHS",
            domain = "general",
        }, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, factory.Translation.MaximumActive);
        Assert.Equal(8, factory.Translation.Commands.Count);
        Assert.All(factory.Translation.Commands, command => Assert.Equal(8, command.BatchSize));
        Assert.Equal(Enumerable.Range(0, 8).Select(index => $"result-{index}"),
            json.RootElement.GetProperty("data").GetProperty("results").EnumerateArray()
                .Select(value => value.GetProperty("content").GetString()));
        Assert.Equal([1, 4, 7, 10, 13, 16, 19, 22],
            factory.Ledger.Attempts.Select(attempt => attempt.AttemptNumber).Order().ToArray());
        Assert.Equal(factory.Ledger.Preparations.Select(value => value.Id).Order(),
            factory.Ledger.Attempts.Select(value => value.Id).Order());
    }

    [Fact]
    public async Task TranslationRetriesOnlyTheFailedItemAndAccountsForEveryAttempt()
    {
        await using var factory = new ApiFactory();
        var calls = new ConcurrentDictionary<int, int>();
        factory.Translation.Handler = (command, _) =>
        {
            calls.AddOrUpdate(command.ItemIndex, 1, (_, count) => count + 1);
            var result = command.ItemIndex == 3 && command.ItemAttemptNumber == 1
                ? FakeTranslationClient.Result(command, success: false, outcome: "provider_http_429",
                    retryable: true, retryAfter: TimeSpan.Zero, status: 429)
                : FakeTranslationClient.Result(command);
            return Task.FromResult(result);
        };
        using var client = factory.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync("/api/v2/translation/translate", new
        {
            type = 0,
            content = Enumerable.Range(0, 8).Select(index => $"item-{index}").ToArray(),
            from = "en",
            to = "zh-CHS",
            domain = "general",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls[3]);
        Assert.All(calls.Where(pair => pair.Key != 3), pair => Assert.Equal(1, pair.Value));
        var initialModel = Assert.Single(factory.Translation.Commands.Where(command => command.ItemAttemptNumber == 1)
            .Select(command => command.Access.LogicalModel).Distinct());
        var retried = factory.Translation.Commands.Where(command => command.ItemIndex == 3)
            .OrderBy(command => command.ItemAttemptNumber).ToArray();
        Assert.Equal(2, retried.Length);
        Assert.Equal(initialModel, retried[0].Access.LogicalModel);
        Assert.NotEqual(initialModel, retried[1].Access.LogicalModel);
        Assert.Equal(9, factory.Ledger.Preparations.Count);
        Assert.Equal(9, factory.Ledger.Attempts.Count);
        var settlement = Assert.Single(factory.Ledger.Settlements);
        Assert.Equal(8 * 180_000, settlement.ReportedPublicCost.Value);
        Assert.Equal(9 * 180_000, settlement.ReportedOperatorCost.Value);
        Assert.Equal([10, 11], factory.Ledger.Attempts.Where(attempt => attempt.AttemptNumber is 10 or 11)
            .Select(attempt => attempt.AttemptNumber).Order().ToArray());
    }

    [Fact]
    public async Task TranslationSwitchesModelsWhenTheInitialModelPoolIsSaturated()
    {
        await using var factory = new ApiFactory();
        var acquisition = 0;
        factory.ProviderAccess.Handler = request => Interlocked.Increment(ref acquisition) == 1
            ? FakeProviderAccessPool.Saturated()
            : FakeProviderAccessPool.Acquired(request);
        using var client = factory.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync("/api/v2/translation/translate", new
        {
            type = 0,
            content = new[] { "item" },
            from = "en",
            to = "zh-CHS",
            domain = "general",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requests = factory.ProviderAccess.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.NotEqual(requests[0].LogicalModel, requests[1].LogicalModel);
        var attempts = factory.Ledger.Attempts.OrderBy(attempt => attempt.AttemptNumber).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal($"{requests[0].LogicalModel}/provider-pool", attempts[0].Provider);
        Assert.Equal($"{requests[1].LogicalModel}/test/aliyun", attempts[1].Provider);
    }

    [Fact]
    public async Task PermanentTranslationFailureDoesNotRetryAndCompletesPreparedAttempts()
    {
        await using var factory = new ApiFactory();
        factory.Translation.Handler = async (command, cancellationToken) =>
        {
            if (command.ItemIndex == 0)
                return FakeTranslationClient.Result(command, success: false, outcome: "provider_http_400",
                    status: 400);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return FakeTranslationClient.Result(command);
        };
        using var client = factory.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync("/api/v2/translation/translate", new
        {
            type = 0,
            content = Enumerable.Range(0, 8).Select(index => $"item-{index}").ToArray(),
            from = "en",
            to = "zh-CHS",
            domain = "general",
        }, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadGateway, "provider_failure", "/api/v2/translation/translate");
        Assert.Single(factory.Translation.Commands, command => command.ItemIndex == 0);
        Assert.Equal(factory.Ledger.Preparations.Count, factory.Ledger.Attempts.Count);
        Assert.Equal(NanoYuan.Zero, Assert.Single(factory.Ledger.Settlements).ReportedPublicCost);
    }

    [Fact]
    public async Task TranslationStopsAfterThreeRetryableAttempts()
    {
        await using var factory = new ApiFactory();
        factory.Translation.Handler = (command, _) => Task.FromResult(FakeTranslationClient.Result(command,
            success: false, outcome: "invalid_output", retryable: true, retryAfter: TimeSpan.Zero, status: 200));
        using var client = factory.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync("/api/v2/translation/translate", new
        {
            type = 0,
            content = new[] { "item" },
            from = "en",
            to = "zh-CHS",
            domain = "general",
        }, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadGateway, "provider_failure", "/api/v2/translation/translate");
        Assert.Equal([1, 2, 3], factory.Translation.Commands.Select(command => command.ItemAttemptNumber).ToArray());
        var attemptedModels = factory.Translation.Commands.Select(command => command.Access.LogicalModel).ToArray();
        Assert.Equal(attemptedModels[0], attemptedModels[2]);
        Assert.NotEqual(attemptedModels[0], attemptedModels[1]);
        Assert.Equal([1, 2, 3], factory.Ledger.Attempts.Select(attempt => attempt.AttemptNumber).ToArray());
        Assert.Equal(factory.Ledger.Preparations.Count, factory.Ledger.Attempts.Count);
    }

    [Fact]
    public async Task TranslationRetriesThrownNetworkFailureAndRejectsRetryDelayBeyondDeadline()
    {
        await using var retryFactory = new ApiFactory();
        retryFactory.Translation.Handler = (command, _) => command.ItemAttemptNumber == 1
            ? Task.FromException<TranslationProviderResult>(new HttpRequestException("network"))
            : Task.FromResult(FakeTranslationClient.Result(command));
        using var retryClient = retryFactory.CreateAnonymousClient();
        var body = new { type = 0, content = new[] { "item" }, from = "en", to = "zh-CHS", domain = "general" };

        using var retryResponse = await retryClient.PostAsJsonAsync("/api/v2/translation/translate", body,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        Assert.Equal(2, retryFactory.Translation.Commands.Count);
        Assert.Equal(AttemptDispatchState.Unknown, retryFactory.Ledger.Attempts.OrderBy(attempt => attempt.AttemptNumber).First().DispatchState);

        await using var deadlineFactory = new ApiFactory();
        deadlineFactory.Translation.Handler = (command, _) => Task.FromResult(FakeTranslationClient.Result(command,
            success: false, outcome: "provider_http_429", retryable: true,
            retryAfter: TimeSpan.FromMinutes(10), status: 429));
        using var deadlineClient = deadlineFactory.CreateAnonymousClient();

        using var deadlineResponse = await deadlineClient.PostAsJsonAsync("/api/v2/translation/translate", body,
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(deadlineResponse, HttpStatusCode.GatewayTimeout, "deadline_exceeded",
            "/api/v2/translation/translate");
        Assert.Single(deadlineFactory.Translation.Commands);
        Assert.Equal(deadlineFactory.Ledger.Preparations.Count, deadlineFactory.Ledger.Attempts.Count);
    }

    [Fact]
    public async Task SameLanguageTranslationRemainsLocalAndOrdered()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync("/api/v2/translation/translate", new
        {
            type = 0,
            content = new[] { "one", "two" },
            from = "en",
            to = "en",
            domain = "general",
        }, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(factory.Translation.Commands);
        Assert.Equal(["one", "two"], json.RootElement.GetProperty("data").GetProperty("results").EnumerateArray()
            .Select(value => value.GetProperty("content").GetString()!).ToArray());
        Assert.Equal("translation-local", Assert.Single(factory.Ledger.Attempts).Provider);
    }

    [Fact]
    public async Task ChatEndsOnlyAfterDurableSettlement()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var response = await client.PostAsJsonAsync("/api/v1/chat/completions", new
        {
            model = "qwen-flash",
            messages = new[] { new { role = "user", content = "hello" } },
            enable_thinking = false,
            temperature = 1,
            max_tokens = 512,
            thinking_budget_tokens = 1024,
        }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("data: {\"id\":\"chat-1\"", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        Assert.True(factory.Ledger.SettlementCompleted);
        var expectedCost = factory.Policy.Get(Resources.QwenFlash).Price.Calculate(100, 20);
        Assert.Equal(expectedCost, Assert.Single(factory.Ledger.Settlements).ReportedPublicCost);
        AssertCompletedAttempt(factory.Ledger, "qwen-flash/test/aliyun");
    }

    [Fact]
    public async Task ChatDoesNotValidateForwardedProviderParameters()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        using var response = await client.PostAsJsonAsync("/api/v1/chat/completions", new
        {
            model = "qwen-flash",
            messages = "provider-defined-shape",
            temperature = "provider-defined-value",
            max_tokens = -1,
            vendor_option = new { enabled = true },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, Assert.Single(factory.Ledger.Reservations).Snapshot.PublicReservation.Value);
    }

    [Fact]
    public async Task ChatFailureAfterFirstFrameUsesSseProblemWithoutDoneMarker()
    {
        await using var factory = new ApiFactory();
        factory.Chat.ThrowAfterFrame = true;
        using var client = factory.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync("/api/v1/chat/completions", new
        {
            model = "qwen-flash",
            messages = new[] { new { role = "user", content = "hello" } },
            enable_thinking = false,
            temperature = 1,
            max_tokens = 512,
            thinking_budget_tokens = 1024,
        }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: error\ndata: ", body, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"internal_error\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("data: [DONE]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TableSuccessPreservesMultipartEnvelopeAndThreeFenCost()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var form = new MultipartFormDataContent("boundary");
        using var image = new ByteArrayContent("RIFF0000WEBP"u8.ToArray());
        image.Headers.ContentType = MediaTypeHeaderValue.Parse("image/webp");
        form.Add(image, "image", "table.webp");
        using var response = await client.PostAsync("/api/v1/table/extract", form, TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<table><tr><td>ok</td></tr></table>", json.RootElement.GetProperty("data").GetProperty("html").GetString());
        Assert.Equal(30_000_000, Assert.Single(factory.Ledger.Settlements).ReportedPublicCost.Value);
        AssertCompletedAttempt(factory.Ledger, "table-worker");
    }

    [Fact]
    public async Task OversizedRequestIdIsRejectedWithoutExecution()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/translation/translate")
        {
            Content = JsonContent.Create(new { type = 0, content = new[] { "one" }, from = "en", to = "zh-CHS", domain = "general" }),
        };
        request.Headers.Add("X-Request-ID", new string('x', 65));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request", "/api/v2/translation/translate");
        Assert.Empty(factory.Ledger.Reservations);
    }

    [Fact]
    public async Task UnsafeRequestIdIsRejectedWithoutForwarding()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/translation/translate")
        {
            Content = JsonContent.Create(new { type = 0, content = new[] { "one" }, from = "en", to = "zh-CHS", domain = "general" }),
        };
        request.Headers.TryAddWithoutValidation("X-Request-ID", "unsafe request id");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request", "/api/v2/translation/translate");
        Assert.Empty(factory.Ledger.Reservations);
    }

    [Fact]
    public async Task MultipleRequestIdValuesAreRejectedAsAmbiguous()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/translation/translate")
        {
            Content = JsonContent.Create(new { type = 0, content = new[] { "one" }, from = "en", to = "zh-CHS", domain = "general" }),
        };
        request.Headers.TryAddWithoutValidation("X-Request-ID", ["first", "second"]);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request", "/api/v2/translation/translate");
        Assert.Empty(factory.Ledger.Reservations);
    }

    [Fact]
    public async Task ValidationErrorsUseStableProblemCodes()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var translation = await client.PostAsJsonAsync("/api/v2/translation/translate",
            new { type = 0, content = Array.Empty<string>(), from = "invalid", to = "en", domain = "general" }, TestContext.Current.CancellationToken);
        using var chat = await client.PostAsJsonAsync("/api/v1/chat/completions",
            new { model = "unknown", messages = new[] { new { role = "user", content = "hello" } } }, TestContext.Current.CancellationToken);
        await AssertProblemAsync(translation, HttpStatusCode.BadRequest, "invalid_request", "/api/v2/translation/translate");
        await AssertProblemAsync(chat, HttpStatusCode.BadRequest, "model_not_found", "/api/v1/chat/completions");
    }

    [Theory]
    [InlineData(ReservationRejectionReason.DuplicateRequest, HttpStatusCode.Conflict, "duplicate_request", false)]
    [InlineData(ReservationRejectionReason.AllowanceExhausted, HttpStatusCode.TooManyRequests, "allowance_exhausted", true)]
    [InlineData(ReservationRejectionReason.OperatorBudgetExhausted, HttpStatusCode.ServiceUnavailable, "operator_budget_exhausted", true)]
    [InlineData(ReservationRejectionReason.PolicyUnavailable, HttpStatusCode.ServiceUnavailable, "policy_unavailable", false)]
    public async Task ReservationRejectionsUseStandardStatusesAndRetryHeaders(
        ReservationRejectionReason rejection,
        HttpStatusCode expectedStatus,
        string expectedCode,
        bool retryable)
    {
        await using var factory = new ApiFactory();
        factory.Ledger.RejectWith = rejection;
        using var client = factory.CreateAnonymousClient();
        using var response = await client.PostAsJsonAsync("/api/v2/translation/translate",
            new { type = 0, content = new[] { "one" }, from = "en", to = "zh-CHS", domain = "general" },
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, expectedStatus, expectedCode, "/api/v2/translation/translate", retryable);
    }

    [Fact]
    public async Task OversizedMultipartUsesPayloadTooLargeStatus()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        using var form = new MultipartFormDataContent("boundary");
        using var image = new ByteArrayContent(new byte[MaximumTableImageBytes + 1]);
        image.Headers.ContentType = MediaTypeHeaderValue.Parse("image/webp");
        form.Add(image, "image", "large.webp");

        using var response = await client.PostAsync("/api/v1/table/extract", form, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large",
            "/api/v1/table/extract");
        Assert.Empty(factory.Ledger.Reservations);
        AssertNoTableExecution(factory);
    }

    [Fact]
    public async Task ExactImageLimitIsAccepted()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        var payload = new byte[MaximumTableImageBytes];
        "RIFF"u8.CopyTo(payload); "WEBP"u8.CopyTo(payload.AsSpan(8));
        using var form = new MultipartFormDataContent("exact-boundary");
        using var image = new ByteArrayContent(payload);
        image.Headers.ContentType = MediaTypeHeaderValue.Parse("image/webp");
        form.Add(image, "image", "exact.webp");

        using var response = await client.PostAsync("/api/v1/table/extract", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Ledger.Reservations);
        Assert.Equal(1, factory.Table.Invocations);
    }

    [Fact]
    public async Task ChunkedImageOverLimitIsRejectedBeforeExecution()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        const string boundary = "chunked-boundary";
        var prefix = Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"x.webp\"\r\nContent-Type: image/webp\r\n\r\n");
        var image = new byte[MaximumTableImageBytes + 1];
        "RIFF"u8.CopyTo(image); "WEBP"u8.CopyTo(image.AsSpan(8));
        var suffix = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/table/extract")
        {
            Content = new ChunkedTestContent([prefix, image, suffix], $"multipart/form-data; boundary={boundary}"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large", "/api/v1/table/extract");
        AssertNoTableExecution(factory);
    }

    [Fact]
    public async Task MultipartOverheadOverLimitIsRejectedBeforeExecution()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        const string boundary = "overhead-boundary";
        var prefix = Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"x.webp\"\r\n\r\n");
        var suffix = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
        var epilogue = new byte[MaximumTableImageBytes + (1024 * 1024)];
        using var content = new ByteArrayContent([.. prefix, .. "RIFF0000WEBP"u8.ToArray(), .. suffix, .. epilogue]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");

        using var response = await client.PostAsync("/api/v1/table/extract", content, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large", "/api/v1/table/extract");
        AssertNoTableExecution(factory);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("wrong_field")]
    [InlineData("duplicate_file")]
    [InlineData("extra_field")]
    [InlineData("non_webp")]
    public async Task InvalidMultipartVariantsAreRejectedBeforeExecution(string variant)
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var form = new MultipartFormDataContent("invalid-boundary");
        var valid = "RIFF0000WEBP"u8.ToArray();
        if (variant == "extra_field")
        {
            form.Add(new ByteArrayContent(valid), "image", "first.webp");
            form.Add(new StringContent("unexpected"), "metadata");
        }
        else
        {
            var payload = variant switch { "empty" => [], "non_webp" => "not-a-webp"u8.ToArray(), _ => valid };
            form.Add(new ByteArrayContent(payload), variant == "wrong_field" ? "other" : "image", "first.webp");
            if (variant == "duplicate_file") form.Add(new ByteArrayContent(valid), "image", "second.webp");
        }

        using var response = await client.PostAsync("/api/v1/table/extract", form, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request", "/api/v1/table/extract");
        AssertNoTableExecution(factory);
    }

    [Fact]
    public async Task MalformedMultipartBoundaryIsRejectedBeforeExecution()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var content = new ByteArrayContent("--missing\r\nContent-Disposition: form-data; name=\"image\"; filename=\"x.webp\"\r\n\r\nRIFF0000WEBP"u8.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=missing");

        using var response = await client.PostAsync("/api/v1/table/extract", content, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request", "/api/v1/table/extract");
        AssertNoTableExecution(factory);
    }

    [Fact]
    public async Task WorkerBusyKeepsPublicEnvelopeAndUsesServiceUnavailable()
    {
        await using var factory = new ApiFactory();
        factory.Table.Status = TableExtractionStatus.Busy;
        using var client = factory.CreateAnonymousClient();
        using var form = new MultipartFormDataContent("boundary");
        using var image = new ByteArrayContent("RIFF0000WEBP"u8.ToArray());
        image.Headers.ContentType = MediaTypeHeaderValue.Parse("image/webp");
        form.Add(image, "image", "table.webp");

        using var response = await client.PostAsync("/api/v1/table/extract", form, TestContext.Current.CancellationToken);
        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "worker_busy",
            "/api/v1/table/extract", retryable: true);
        Assert.Equal(NanoYuan.Zero, Assert.Single(factory.Ledger.Settlements).ReportedOperatorCost);
    }

    [Fact]
    public async Task FailedDurableSettlementNeverEmitsDone()
    {
        await using var factory = new ApiFactory();
        factory.Ledger.RejectSettlementWith = SettlementRejectionReason.LeaseLost;
        using var client = factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "qwen-flash",
                messages = new[] { new { role = "user", content = "hello" } },
                max_tokens = 512,
                thinking_budget_tokens = 1024,
            }),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: error\ndata: ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("data: [DONE]", body, StringComparison.Ordinal);
        var problemLine = Assert.Single(body.Split('\n'), value =>
            value.StartsWith("data: {\"type\":\"urn:snowshot:problem:", StringComparison.Ordinal));
        using var problem = JsonDocument.Parse(problemLine[6..]);
        Assert.Equal("ownership_lost", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal((int)HttpStatusCode.Conflict, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("/api/v1/chat/completions", problem.RootElement.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("trace_id").GetString()));
    }

    private static void AssertCompletedAttempt(FakeLedger ledger, string provider)
    {
        var preparation = Assert.Single(ledger.Preparations);
        var attempt = Assert.Single(ledger.Attempts);
        Assert.Equal(preparation.Id, attempt.Id);
        Assert.Equal(preparation.Handle.OperationId, attempt.OperationId);
        Assert.Equal(provider, preparation.Provider);
        Assert.Equal(provider, attempt.Provider);
        Assert.Equal(AttemptDispatchState.Dispatched, attempt.DispatchState);
    }

    private static void AssertNoTableExecution(ApiFactory factory)
    {
        Assert.Empty(factory.Ledger.Reservations);
        Assert.Empty(factory.Ledger.Preparations);
        Assert.Empty(factory.Ledger.Attempts);
        Assert.Empty(factory.Ledger.Settlements);
        Assert.Equal(0, factory.Table.Invocations);
    }

    private sealed class ChunkedTestContent : HttpContent
    {
        private readonly IReadOnlyList<byte[]> _chunks;
        public ChunkedTestContent(IReadOnlyList<byte[]> chunks, string contentType)
        {
            _chunks = chunks;
            Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            foreach (var chunk in _chunks) await stream.WriteAsync(chunk);
        }
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(_chunks.SelectMany(value => value).ToArray()));
    }

    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        string instance,
        bool retryable = false)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var problem = document.RootElement;
        Assert.Equal($"urn:snowshot:problem:{code}", problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.Equal(instance, problem.GetProperty("instance").GetString());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("trace_id").GetString()));
        Assert.Equal(retryable, response.Headers.RetryAfter is not null);
        Assert.Equal(retryable, problem.TryGetProperty("retry_after_seconds", out var retryAfter));
        if (retryable) Assert.True(retryAfter.GetInt32() >= 1);
        return problem.Clone();
    }
}
