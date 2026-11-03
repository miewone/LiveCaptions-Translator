using System.Text.Json.Serialization;

namespace LiveCaptionsTranslator.models;

/// <summary>Non-secret ChatGPT translation settings. Credentials are stored separately in the Windows vault.</summary>
public sealed class ChatGptOAuthConfig : TranslateAPIConfig, IJsonOnDeserialized
{
    private string modelName = "";
    private bool? legacyFastTranslation;

    /// <summary>Reads the previous speed checkbox for migration; new settings no longer write this property.</summary>
    [JsonPropertyName("FastTranslation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyFastTranslation
    {
        get => null;
        set => legacyFastTranslation = value;
    }

    /// <summary>Saved reasoning overrides keyed by exact model ID.</summary>
    public Dictionary<string, string> ModelReasoningEfforts { get; set; } = [];
    /// <summary>Account-specific catalogs and last selections keyed by non-secret client registration ID.</summary>
    public Dictionary<string, ChatGptModelCatalog> ModelCatalogs { get; set; } = [];
    /// <summary>Account owning the active model selection; credentials remain in the separate Windows vault.</summary>
    public string CatalogAccountId { get; set; } = "";

    /// <summary>Choices available for the currently selected model.</summary>
    [JsonIgnore]
    public IReadOnlyList<ChatGptReasoningOption> ReasoningOptions => ChatGptReasoning.Options(ModelName);

    /// <summary>Gets or changes only the current model's saved reasoning level.</summary>
    [JsonIgnore]
    public string ReasoningEffort
    {
        get => GetReasoningEffort(ModelName);
        set
        {
            if (string.IsNullOrEmpty(ModelName) || !ChatGptReasoning.IsAllowed(ModelName, value) || value == ReasoningEffort) return;
            ModelReasoningEfforts = new(ModelReasoningEfforts) { [ModelName] = value };
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the selected account has a successfully fetched catalog, including an empty one.</summary>
    [JsonIgnore]
    public bool HasCachedCatalog => ModelCatalogs.TryGetValue(CatalogAccountId, out var catalog) && catalog.UpdatedAt != null;

    /// <summary>Saved choices for the current account; an older saved model remains visible before the first catalog refresh.</summary>
    [JsonIgnore]
    public IReadOnlyList<ChatGptModel> CachedModels => HasCachedCatalog
        ? ModelCatalogs[CatalogAccountId].Models.AsReadOnly()
        : string.IsNullOrEmpty(ModelName) ? Array.Empty<ChatGptModel>() : new[] { new ChatGptModel(ModelName, ModelName) };

    /// <summary>The model slug selected from the active ChatGPT account's catalog.</summary>
    public string ModelName
    {
        get => modelName;
        set
        {
            if (modelName == value) return;
            modelName = value;
            if (ModelCatalogs.TryGetValue(CatalogAccountId, out var catalog)) catalog.SelectedModel = value;
            OnPropertyChanged(string.Empty);
        }
    }

    /// <summary>Gets the saved override for a captured model, preventing a concurrent UI switch from mixing model settings.</summary>
    /// <param name="model">Model slug used for the pending request.</param>
    public string GetReasoningEffort(string model) => ModelReasoningEfforts.TryGetValue(model, out var effort) &&
        ChatGptReasoning.IsAllowed(model, effort) ? effort : "default";

    /// <summary>Restores one account's last selection without fetching or borrowing another account's catalog.</summary>
    /// <param name="clientId">Active client registration, or empty when signed out.</param>
    public void SelectCatalogAccount(string clientId)
    {
        if (CatalogAccountId == clientId) return;
        var legacySelection = CatalogAccountId == "" && ModelCatalogs.Count == 0 ? modelName : "";
        CatalogAccountId = clientId;
        if (clientId.Length == 0) modelName = "";
        else
        {
            if (!ModelCatalogs.TryGetValue(clientId, out var catalog))
                ModelCatalogs[clientId] = catalog = new() { SelectedModel = legacySelection };
            modelName = catalog.SelectedModel;
        }
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Atomically replaces an account's catalog after a successful fetch, preserving its selection if still available.</summary>
    /// <param name="clientId">Registration whose token fetched these models.</param>
    /// <param name="models">Visible model entries in server order.</param>
    public void StoreCatalog(string clientId, IEnumerable<ChatGptModel> models)
    {
        var entries = models.ToList();
        var selected = clientId == CatalogAccountId ? modelName :
            ModelCatalogs.TryGetValue(clientId, out var old) ? old.SelectedModel : "";
        if (!entries.Any(x => x.Id == selected)) selected = "";
        ModelCatalogs[clientId] = new() { Models = entries, SelectedModel = selected, UpdatedAt = DateTimeOffset.UtcNow };
        if (clientId == CatalogAccountId) modelName = selected;
        OnPropertyChanged(string.Empty);
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (legacyFastTranslation == true && !ModelReasoningEfforts.ContainsKey("gpt-6.1-terra"))
            ModelReasoningEfforts["gpt-6.1-terra"] = "none";
        legacyFastTranslation = null;
    }
}
