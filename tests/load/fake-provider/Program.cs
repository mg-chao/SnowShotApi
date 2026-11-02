using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton<ProviderState>();
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet("/metrics", (ProviderState state) => Results.Ok(state.Snapshot()));
app.MapPost("/control/reset", (ProviderState state) =>
    state.TryReset() ? Results.Ok(state.Snapshot()) : Results.Conflict(new { error = "requests_active" }));
app.MapPost("/v1/chat/completions", async (HttpContext context, ProviderState state) =>
{
    using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
    state.Start();
    try
    {
        var raw = document.RootElement.GetRawText();
        var match = Regex.Match(raw, "delay=(?<seconds>[0-9]+(?:\\.[0-9]+)?)", RegexOptions.CultureInvariant);
        if (match.Success)
        {
            var seconds = Math.Min(double.Parse(match.Groups["seconds"].Value, System.Globalization.CultureInfo.InvariantCulture), 180);
            await Task.Delay(TimeSpan.FromSeconds(seconds), context.RequestAborted);
        }

        if (document.RootElement.GetProperty("stream").GetBoolean())
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"id\":\"fault-harness\",\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n", context.RequestAborted);
            await context.Response.WriteAsync("data: {\"id\":\"fault-harness\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}\n\n", context.RequestAborted);
            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
            await context.Response.CompleteAsync();
            return;
        }

        var user = document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()
            ?? throw new InvalidDataException("Translation content is missing.");
        using var items = JsonDocument.Parse(user);
        var indexedItems = items.RootElement.EnumerateArray().Select(item => new
        {
            index = item.GetProperty("index").GetInt32(),
            content = item.GetProperty("content").GetString() ?? string.Empty,
        }).ToArray();
        if (indexedItems.Length != 1) throw new InvalidDataException("Expected one translation item per conversation.");
        state.Record(indexedItems[0].content);
        if (indexedItems[0].content.Contains("fail-once", StringComparison.Ordinal) &&
            state.ShouldFailOnce(indexedItems[0].content))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "0";
            await context.Response.WriteAsync("retry", context.RequestAborted);
            return;
        }
        var translations = indexedItems.Select(item => new
        {
            item.index,
            content = $"translated:{item.content}",
        }).ToArray();
        var content = JsonSerializer.Serialize(new { translations });
        await context.Response.WriteAsJsonAsync(new
        {
            id = "fault-harness",
            choices = new[] { new { message = new { content } } },
        }, context.RequestAborted);
    }
    finally
    {
        state.Complete();
    }
});

app.Run();

internal sealed class ProviderState
{
    private int _active;
    private int _maximumActive;
    private int _requests;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

    public void Start()
    {
        var active = Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _requests);
        var maximum = Volatile.Read(ref _maximumActive);
        while (active > maximum)
        {
            maximum = Interlocked.CompareExchange(ref _maximumActive, active, maximum);
        }
    }

    public void Complete() => Interlocked.Decrement(ref _active);
    public void Record(string content) => _calls.AddOrUpdate(content, 1, (_, count) => count + 1);
    public bool ShouldFailOnce(string content) => _calls[content] == 1;

    public object Snapshot() => new
    {
        active = Volatile.Read(ref _active),
        max_active = Volatile.Read(ref _maximumActive),
        requests = Volatile.Read(ref _requests),
        calls = _calls.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
    };

    public bool TryReset()
    {
        if (Volatile.Read(ref _active) != 0) return false;
        Interlocked.Exchange(ref _maximumActive, 0);
        Interlocked.Exchange(ref _requests, 0);
        _calls.Clear();
        return true;
    }
}
