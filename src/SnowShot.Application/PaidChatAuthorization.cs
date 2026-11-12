using System.Text.Json;
using System.Text.Json.Nodes;
using SnowShot.Domain;

namespace SnowShot.Application;

public sealed record PaidChatAuthorization(ChatCommand Request, NanoYuan MaximumCharge);

public static class PaidChatAuthorizer
{
    // Qwen3.8 supports up to 16,384 visual tokens plus two image delimiters per image.
    private const int ExpertTokensPerImage = 16_386;
    private static readonly HashSet<string> SupportedFields = new(StringComparer.Ordinal)
    {
        "model", "messages", "stream", "stream_options", "enable_thinking", "max_tokens",
        "max_completion_tokens", "thinking_budget_tokens", "thinking_budget", "temperature",
        "top_p", "presence_penalty", "frequency_penalty", "repetition_penalty", "stop",
        "seed", "response_format", "n", "incremental_output", "translation_options",
    };

    public static PaidChatAuthorization Authorize(ChatCommand command, IChatModelCatalog catalog, ServicePolicy policy)
    {
        var envelope = catalog.BillingEnvelope(command.ExecutionModel)
            ?? throw new InvalidOperationException("Paid billing limits are unavailable for this model.");
        var pricing = policy.PricingFor(command.ExecutionModel)
            ?? throw new InvalidOperationException("Paid billing tariff is unavailable for this model.");
        if (JsonNode.Parse(command.Utf8Json.Span) is not JsonObject root)
            throw new JsonException("A chat object is required.");
        if (root.Any(field => !SupportedFields.Contains(field.Key)))
            throw new JsonException("The request contains a feature without a paid billing envelope.");
        if (Integer(root, "n") is { } n && n != 1)
            throw new JsonException("Paid requests support exactly one completion.");
        var thinking = Boolean(root, "enable_thinking") ?? false;
        var ceiling = thinking ? 32_768 : 8_192;
        var total = Integer(root, "max_completion_tokens");
        var answer = Integer(root, "max_tokens");
        var requested = total ?? answer ?? ceiling;
        if (requested < 1 || requested > ceiling)
            throw new JsonException("The completion limit exceeds the paid billing envelope.");
        if (total is not null && answer is not null && total != answer)
            throw new JsonException("Conflicting completion limits are not supported.");
        if (root["messages"] is not JsonArray messages || messages.Count == 0)
            throw new JsonException("A nonempty messages array is required.");
        var expertImages = command.Model == Resources.ExpertModel ? new List<JsonObject>() : null;
        var hasImages = false;
        foreach (var item in messages)
        {
            if (item is not JsonObject message || Text(message, "role") is not ("system" or "user" or "assistant") ||
                message.Any(field => field.Key is not ("role" or "content" or "name")))
                throw new JsonException("Paid messages support text and image input only.");
            if (message["content"] is JsonArray parts)
                foreach (var part in parts)
                {
                    if (part is not JsonObject content || Text(content, "type") is not ("text" or "image_url"))
                        throw new JsonException("The input modality has no paid billing envelope.");
                    if (Text(content, "type") == "image_url")
                    {
                        hasImages = true;
                        if (expertImages is not null)
                        {
                            if (content.Any(field => field.Key is not ("type" or "image_url")) ||
                                content["image_url"] is not JsonObject image || Text(image, "url") is null ||
                                image.Any(field => field.Key is not ("url" or "detail")))
                                throw new JsonException("Expert image parts must contain a standard image URL.");
                            expertImages.Add(image);
                        }
                    }
                    else if (Text(content, "text") is null || content.Any(field => field.Key is not ("type" or "text")))
                        throw new JsonException("Text parts must contain only their type and text.");
                }
            if (message["content"] is not null and not JsonArray &&
                (message["content"] is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw new JsonException("Message content must be text or a content array.");
        }
        root.Remove("max_tokens");
        root.Remove("thinking_budget_tokens");
        root.Remove("thinking_budget");
        root["enable_thinking"] = thinking;
        root["max_completion_tokens"] = requested;
        root["n"] = 1;
        var payload = JsonSerializer.SerializeToUtf8Bytes(root);
        // Provider-injected schemas and native translation prompts use the full context envelope.
        var boundedInput = (!hasImages || expertImages is not null) && !root.ContainsKey("translation_options") &&
            (root["response_format"] is null || root["response_format"] is JsonObject format &&
                Text(format, "type") is "text" or "json_object" && format.Count == 1);
        long? inputBound = null;
        if (boundedInput && envelope.TextInput is { } textEnvelope)
        {
            var textBytes = payload.Length;
            if (expertImages is { Count: > 0 })
            {
                // The upstream consumes image URLs as images. Keep their framing in the text bound.
                foreach (var image in expertImages)
                    image["url"] = "";
                textBytes = JsonSerializer.SerializeToUtf8Bytes(root).Length;
            }
            inputBound = textEnvelope.MaximumInputTokens(textBytes, messages.Count) +
                (long)(expertImages?.Count ?? 0) * ExpertTokensPerImage;
        }
        return new(command with { Utf8Json = payload }, envelope.MaximumCharge(pricing.Tariff, thinking, requested, inputBound));
    }

    private static int? Integer(JsonObject source, string name)
    {
        if (!source.TryGetPropertyValue(name, out var node)) return null;
        if (node is JsonValue value && value.TryGetValue<int>(out var result)) return result;
        throw new JsonException($"{name} must be an integer.");
    }

    private static bool? Boolean(JsonObject source, string name)
    {
        if (!source.TryGetPropertyValue(name, out var node)) return null;
        if (node is JsonValue value && value.TryGetValue<bool>(out var result)) return result;
        throw new JsonException($"{name} must be a boolean.");
    }

    private static string? Text(JsonObject source, string name)
    {
        if (source[name] is JsonValue value && value.TryGetValue<string>(out var result)) return result;
        return null;
    }
}
