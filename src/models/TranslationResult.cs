namespace LiveCaptionsTranslator.models;

/// <summary>A short candidate reply in the speaker's language and its Korean meaning.</summary>
/// <param name="Text">Text the user can say or copy.</param>
/// <param name="Meaning">Korean explanation of the reply's meaning.</param>
public sealed record ReplySuggestion(string Text, string Meaning);

/// <summary>A translation and optional reply suggestions from the same model response.</summary>
/// <param name="Text">Only the translated caption, suitable for history and context.</param>
/// <param name="IsChoke">Whether the legacy caption display should pause briefly.</param>
public sealed record TranslationResult(string Text, bool IsChoke = false)
{
    /// <summary>Validated reply choices, empty for ordinary translations.</summary>
    public IReadOnlyList<ReplySuggestion> Replies { get; init; } = Array.Empty<ReplySuggestion>();
    /// <summary>Whether this request asked for reply choices.</summary>
    public bool RepliesRequested { get; init; }
}
