namespace LiveCaptionsTranslator.models;

/// <summary>A non-secret model entry from the selected ChatGPT account's catalog.</summary>
/// <param name="Id">Model slug sent to the Responses API.</param>
/// <param name="DisplayName">Human-readable name shown in the model picker.</param>
public sealed record ChatGptModel(string Id, string DisplayName);

/// <summary>Persisted model catalog and last selection for one OAuth client registration; contains no credentials.</summary>
public sealed class ChatGptModelCatalog
{
    /// <summary>Models in server-provided order.</summary>
    public List<ChatGptModel> Models { get; set; } = [];
    /// <summary>The user's last selected model for this account.</summary>
    public string SelectedModel { get; set; } = "";
    /// <summary>Last successful refresh; null distinguishes an uninitialized catalog from an empty catalog.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>A selectable reasoning level and its UI label.</summary>
/// <param name="Value">API value, or default to omit the override.</param>
/// <param name="Label">Display text for the selector.</param>
public sealed record ChatGptReasoningOption(string Value, string Label);

/// <summary>Reasoning choices with constraints for documented models and explicit overrides for other catalog models.</summary>
public static class ChatGptReasoning
{
    private static readonly ChatGptReasoningOption[] all =
    [
        new("default", "모델 기본값"), new("none", "none · 추론 없음"), new("minimal", "minimal · 최소"),
        new("low", "low · 낮음"), new("medium", "medium · 보통"), new("high", "high · 높음"),
        new("xhigh", "xhigh · 매우 높음"), new("max", "max · 최대")
    ];

    /// <summary>Returns reasoning choices for a model, excluding known unsupported levels.</summary>
    /// <param name="model">Exact model slug from the account catalog.</param>
    public static IReadOnlyList<ChatGptReasoningOption> Options(string model) => model switch
    {
        "" => all.Where(x => x.Value == "default").ToArray(),
        "gpt-6.1-terra" or "gpt-6-sol" or "gpt-6-luna" => all.Where(x => x.Value != "minimal").ToArray(),
        "gpt-6.1-sol" or "gpt-6-astra" => all.Where(x => x.Value is not ("none" or "minimal")).ToArray(),
        _ => Array.AsReadOnly(all)
    };

    /// <summary>Validates an explicit reasoning value before constructing an inference request.</summary>
    /// <param name="model">Selected model slug.</param>
    /// <param name="effort">User-selected override, or default.</param>
    public static bool IsAllowed(string model, string effort) => Options(model).Any(x => x.Value == effort);
}
