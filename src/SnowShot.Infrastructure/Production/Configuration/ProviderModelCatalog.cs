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
    private readonly Dictionary<string, bool> _mergesSystemIntoUser;
    private readonly Dictionary<string, bool> _nativeTranslationOptions;
    private readonly HashSet<string> _translationModels;
    private readonly ChatModelDefinition[] _chatModels;
    private readonly Dictionary<string, (string PublicModel, string TextModel, string? VisionModel)> _routes = new(StringComparer.Ordinal);

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
        var logicalModels = translation.ConfiguredLogicalModels;
        if (logicalModels.Count == 0 ||
            logicalModels.Distinct(StringComparer.Ordinal).Count() != logicalModels.Count ||
            logicalModels.Any(model => !options.Models.ContainsKey(model)))
            throw new InvalidOperationException(
                "Providers:Translation:LogicalModels must contain unique configured models.");

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
        _mergesSystemIntoUser = options.Models.ToDictionary(
            model => model.Key, model => model.Value?.MergesSystemIntoUser == true, StringComparer.Ordinal);
        _nativeTranslationOptions = options.Models.ToDictionary(
            model => model.Key, model => model.Value?.NativeTranslationOptions == true, StringComparer.Ordinal);

        // Models designated for the translation service are also offered for chat,
        // flagged so clients can present them as translation-specialized.
        _translationModels = logicalModels.ToHashSet(StringComparer.Ordinal);
        _chatModels = options.Models
            .OrderBy(model => model.Value.Order).ThenBy(model => model.Key, StringComparer.Ordinal)
            .Select(model => new ChatModelDefinition(model.Key, model.Value.Thinking,
                string.IsNullOrWhiteSpace(model.Value.TranslationMode) ? "default" : model.Value.TranslationMode,
                model.Value.SupportVision, _translationModels.Contains(model.Key)))
            .ToArray();
        if (options.PublicModels.Count > 0)
        {
            if (options.PublicModels.TryGetValue(Resources.AiModel, out var aiModel) &&
                (aiModel is null || aiModel.VisionModel is null || logicalModels.Count != 1 ||
                    logicalModels[0] != aiModel.TextModel))
                throw new InvalidOperationException("AI Model requires dedicated translation to share its single text route.");
            foreach (var (id, model) in options.PublicModels)
            {
                ValidateIdentifier(id, "public model");
                if (model is null || model.Order < 0 || !_models.ContainsKey(model.TextModel) ||
                    (model.VisionModel is not null && (!_models.ContainsKey(model.VisionModel) ||
                        !options.Models[model.VisionModel].SupportVision)))
                    throw new InvalidOperationException($"Public model '{id}' has invalid execution routes.");
                foreach (var alias in model.Aliases.Prepend(id))
                {
                    ValidateIdentifier(alias, "model alias");
                    if (!_routes.TryAdd(alias, (id, model.TextModel, model.VisionModel)))
                        throw new InvalidOperationException($"Public model alias '{alias}' is ambiguous.");
                }
            }
            _chatModels = options.PublicModels.OrderBy(pair => pair.Value.Order).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ChatModelDefinition(pair.Key, pair.Value.Thinking, "default",
                    pair.Value.VisionModel is not null, false)).ToArray();
        }
    }

    public IReadOnlyList<ChatModelDefinition> Models => _chatModels;

    public bool Contains(string model) => _routes.Count > 0 ? _routes.ContainsKey(model) : _models.ContainsKey(model);

    public string CanonicalModel(string model) => _routes.TryGetValue(model, out var route) ? route.PublicModel : model;

    public string ExecutionModel(string model, bool hasImages) => _routes.TryGetValue(model, out var route)
        ? hasImages ? route.VisionModel ?? throw new InvalidOperationException("The model does not support images.") : route.TextModel
        : model;

    public IEnumerable<string> ChatExecutionModels => _routes.Count > 0
        ? _routes.Values.SelectMany(route => route.VisionModel is null ? new[] { route.TextModel } : new[] { route.TextModel, route.VisionModel })
            .Distinct(StringComparer.Ordinal)
        : _models.Keys;

    public string RoutingIdentity => _routes.Count == 0 ? "" :
        string.Join('\n', _routes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}|{pair.Value.PublicModel}|{pair.Value.TextModel}|{pair.Value.VisionModel}")) +
        "\n" + string.Join('\n', All.OrderBy(value => value.Selection.AttemptProvider, StringComparer.Ordinal)
            .Select(value => $"{value.Selection.AttemptProvider}|{value.Selection.UpstreamModel}"));

    public string TranslationMode(string model) =>
        Models.FirstOrDefault(value => value.Model == model)?.TranslationMode ?? "default";

    public bool IsTranslationModel(string model) => _translationModels.Contains(model);

    public bool MergesSystemIntoUser(string model) =>
        _mergesSystemIntoUser.TryGetValue(model, out var merges) && merges;

    public bool UsesNativeTranslationOptions(string model) =>
        _nativeTranslationOptions.TryGetValue(model, out var native) && native;

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
