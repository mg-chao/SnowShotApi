using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnowShot.Contracts;

public static class ApiVersions
{
    public const string Chat = "v1";
    public const string Translation = "v2";
    public const string Table = "v1";
}

public sealed record AppEnvelope
{
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }

    public static AppEnvelope Success(object data, string message = "Request success") =>
        new() { Code = PublicCodes.Success, Message = message, Data = data };

    public static AppEnvelope Error(int code, string message) =>
        new() { Code = code, Message = message, Data = null };
}

public static class PublicCodes
{
    public const int Success = 0;
}

public sealed class ChatRequest
{
    [JsonPropertyName("model"), Required]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; init; } = [];

    [JsonPropertyName("enable_thinking")]
    public bool EnableThinking { get; init; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 1;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; } = 4096;

    [JsonPropertyName("thinking_budget_tokens")]
    public int ThinkingBudgetTokens { get; init; } = 4096;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public JsonElement Content { get; init; }
}

public sealed record ChatModelDescriptor(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("thinking")] bool Thinking,
    [property: JsonPropertyName("support_vision")] bool SupportVision);

public sealed record ChatUsage(
    [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
    [property: JsonPropertyName("completion_tokens")] long CompletionTokens,
    [property: JsonPropertyName("total_tokens")] long TotalTokens,
    long ReasoningTokens = 0);

public enum TranslationType
{
    AI = 0,
}

public sealed class TranslationRequest
{
    [JsonPropertyName("type")]
    public TranslationType Type { get; init; }

    [JsonPropertyName("content")]
    public List<string?> Content { get; init; } = [];

    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;
}

public sealed record TranslationContent(
    [property: JsonPropertyName("content")] string Content);

public sealed record TranslationResponseData(
    [property: JsonPropertyName("results")] IReadOnlyList<TranslationContent> Results,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To);

public sealed record TranslationTypeOption(
    [property: JsonPropertyName("type")] TranslationType Type,
    [property: JsonPropertyName("name")] string Name);

public sealed record TableExtractionData(
    [property: JsonPropertyName("html")] string Html);
