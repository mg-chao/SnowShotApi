using SnowShot.Application;
using SnowShot.Domain;

namespace SnowShot.Infrastructure.Configuration;

public sealed record ProviderAccessDefinition(
    ProviderAccessSelection Selection,
    Uri Endpoint,
    string ApiKey,
    int MaxConcurrentRequests,
    bool? TranslationEnableThinking);

public sealed record CloudProviderDefinition(
    string Name,
    Uri Endpoint,
    string ApiKey,
    bool? TranslationEnableThinking);

public sealed class ProviderModelCatalog : IChatModelCatalog
{
    private readonly Dictionary<string, CloudProviderDefinition> _providers;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, ProviderAccessDefinition>> _models;
    private readonly IReadOnlyList<ChatModelDefinition> _chatModels;

    public ProviderModelCatalog(ProviderModelsOptions options, TranslationProviderOptions translation, bool requireHttps)
    {
        if (options.CloudProviders.Count == 0)
            throw new InvalidOperationException("Providers:CloudProviders must configure at least one cloud service provider.");

        _providers = options.CloudProviders.ToDictionary(provider => provider.Key, provider =>
        {
            ValidateIdentifier(provider.Key, "provider");
            var value = provider.Value;
            if (value is null || string.IsNullOrWhiteSpace(value.ApiKey) ||
                !Uri.TryCreate(value.Endpoint, UriKind.Absolute, out var endpoint) ||
                (requireHttps && endpoint.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException($"Cloud provider '{provider.Key}' is incomplete or invalid.");
            return new CloudProviderDefinition(provider.Key, endpoint, value.ApiKey, value.TranslationEnableThinking);
        }, StringComparer.Ordinal);

        if (options.Models.Count == 0)
            throw new InvalidOperationException("Providers:Models must configure at least one public chat model.");
        if (!options.Models.ContainsKey(translation.LogicalModel))
            throw new InvalidOperationException("Providers:Translation:LogicalModel must reference a configured model.");

        _models = options.Models.ToDictionary(model => model.Key, model =>
        {
            ValidateIdentifier(model.Key, "model");
            if (model.Value is not null && model.Value.Order < 0)
                throw new InvalidOperationException($"Provider model '{model.Key}' has a negative display order.");
            if (model.Value is null || model.Value.Accesses.Count == 0)
                throw new InvalidOperationException($"Provider model '{model.Key}' requires at least one access entry.");
            return (IReadOnlyDictionary<string, ProviderAccessDefinition>)model.Value.Accesses.ToDictionary(access => access.Key, access =>
            {
                ValidateIdentifier(access.Key, "access ID");
                var value = access.Value;
                if (value is null || string.IsNullOrWhiteSpace(value.Provider) ||
                    string.IsNullOrWhiteSpace(value.UpstreamModel) || value.MaxConcurrentRequests <= 0)
                    throw new InvalidOperationException($"Provider access '{model.Key}/{access.Key}' is incomplete or invalid.");
                ValidateIdentifier(value.Provider, "provider");
                if (!_providers.TryGetValue(value.Provider, out var provider))
                    throw new InvalidOperationException($"Provider access '{model.Key}/{access.Key}' references unknown cloud provider '{value.Provider}'.");
                var selection = new ProviderAccessSelection(model.Key, access.Key, value.Provider, value.UpstreamModel);
                if (selection.AttemptProvider.Length > 64)
                    throw new InvalidOperationException($"Provider identity '{selection.AttemptProvider}' exceeds 64 characters.");
                return new ProviderAccessDefinition(selection, provider.Endpoint, provider.ApiKey, value.MaxConcurrentRequests,
                    provider.TranslationEnableThinking);
            }, StringComparer.Ordinal);
        }, StringComparer.Ordinal);

        _chatModels = options.Models.OrderBy(model => model.Value.Order).ThenBy(model => model.Key, StringComparer.Ordinal)
            .Select(model => new ChatModelDefinition(model.Key, model.Value.Thinking, model.Value.SupportVision))
            .ToArray();
    }

    public IReadOnlyList<ChatModelDefinition> Models => _chatModels;

    public bool Contains(string model) => _models.ContainsKey(model);

    public IReadOnlyList<ProviderAccessSelection> Selections(string logicalModel) =>
        Model(logicalModel).Values.Select(value => value.Selection).OrderBy(value => value.AccessId, StringComparer.Ordinal).ToArray();

    public ProviderAccessDefinition Get(string logicalModel, string accessId) =>
        Model(logicalModel).TryGetValue(accessId, out var access)
            ? access
            : throw new KeyNotFoundException($"Provider access '{logicalModel}/{accessId}' is not configured.");

    public int GetMaxConcurrentRequests(ProviderAccessSelection selection) =>
        Get(selection.LogicalModel, selection.AccessId).MaxConcurrentRequests;

    public IEnumerable<ProviderAccessDefinition> All => _models.Values.SelectMany(value => value.Values);

    private IReadOnlyDictionary<string, ProviderAccessDefinition> Model(string logicalModel) =>
        _models.TryGetValue(logicalModel, out var model)
            ? model
            : throw new KeyNotFoundException($"Provider model '{logicalModel}' is not configured.");

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new InvalidOperationException($"Provider {name} '{value}' must contain 1-32 ASCII letters, digits, '.', '_' or '-'.");
    }
}
