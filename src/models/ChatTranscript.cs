using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.models;

/// <summary>Lifecycle of a caption shown in the chat transcript.</summary>
public enum ChatUpdateKind { Preview, Completed, Canceled, Error, Started }

/// <summary>An ordered caption update, containing source and translation from the same request.</summary>
/// <param name="Sequence">Monotonically increasing translation request identifier.</param>
/// <param name="Source">Source caption belonging to this request.</param>
/// <param name="Translation">Accumulated translation or a user-facing error.</param>
/// <param name="Kind">Whether this update starts a request, is provisional, final, canceled, or failed.</param>
/// <param name="Overwrite">Whether the final caption revises the last completed sentence.</param>
public sealed record ChatUpdate(long Sequence, string Source, string Translation, ChatUpdateKind Kind, bool Overwrite = false)
{
    /// <summary>Reply choices from the same completed request as the translation.</summary>
    public IReadOnlyList<ReplySuggestion> Replies { get; init; } = Array.Empty<ReplySuggestion>();
    /// <summary>Whether the request asked for reply choices.</summary>
    public bool RepliesRequested { get; init; }
}

/// <summary>One immutable displayed voice-caption pair; time marks when the row first appeared.</summary>
/// <param name="Source">Original voice caption.</param>
/// <param name="Translation">Translated text.</param>
/// <param name="ReceivedAt">Local display timestamp.</param>
/// <param name="IsStreaming">True while the translation is incomplete.</param>
public sealed record ChatMessage(string Source, string Translation, DateTimeOffset ReceivedAt, bool IsStreaming)
{
    /// <summary>Compact local timestamp for the chat gutter.</summary>
    public string TimeLabel => ReceivedAt.ToLocalTime().ToString("HH:mm");
    /// <summary>Optional short replies paired with this caption.</summary>
    public IReadOnlyList<ReplySuggestion> Replies { get; init; } = Array.Empty<ReplySuggestion>();
    /// <summary>Whether the reply panel contains choices.</summary>
    public bool HasReplies => Replies.Count > 0;
    /// <summary>True when translation succeeded but the model did not return three usable reply choices.</summary>
    public bool RepliesUnavailable { get; init; }
}

/// <summary>Bounded chat presentation state shared by the main window and overlay. Mutate on the UI thread.</summary>
public sealed class ChatTranscript : INotifyPropertyChanged
{
    private readonly int capacity;
    private readonly ObservableCollection<ChatMessage> messages = [];
    private long latestSequence;
    private long completedSequence;
    private ChatMessage? liveMessage;
    private string status = "음성을 기다리는 중";
    private bool isPaused;
    private bool hasError;

    /// <summary>Creates a transcript retaining at most <paramref name="capacity"/> completed rows.</summary>
    public ChatTranscript(int capacity = 50)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        Messages = new ReadOnlyObservableCollection<ChatMessage>(messages);
    }

    /// <summary>Completed messages, oldest first. Persistent history remains in the existing database.</summary>
    public ReadOnlyObservableCollection<ChatMessage> Messages { get; }
    /// <summary>The newest source caption and any provisional translation, separate from completed history.</summary>
    public ChatMessage? LiveMessage => liveMessage;
    /// <summary>True before any voice captions have arrived.</summary>
    public bool IsEmpty => messages.Count == 0 && liveMessage == null;
    /// <summary>Short activity or actionable failure message.</summary>
    public string Status => status;
    /// <summary>Whether translation is paused.</summary>
    public bool IsPaused => isPaused;
    /// <summary>Whether the status describes a failed request.</summary>
    public bool HasError => hasError;
    /// <summary>Notifies WPF bindings when chat presentation state changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Applies an ordered queue event, rejecting stale previews and replacing revised final captions.</summary>
    public void Apply(ChatUpdate update)
    {
        if (isPaused) return;
        switch (update.Kind)
        {
            case ChatUpdateKind.Started:
            case ChatUpdateKind.Preview:
                if (update.Sequence < latestSequence || update.Sequence <= completedSequence) return;
                var timestamp = update.Sequence == latestSequence && liveMessage != null ? liveMessage.ReceivedAt : DateTimeOffset.Now;
                latestSequence = update.Sequence;
                liveMessage = new(update.Source, Clean(update.Translation), timestamp, true);
                status = "번역 중…";
                hasError = false;
                break;
            case ChatUpdateKind.Completed:
                if (update.Sequence <= completedSequence) return;
                completedSequence = update.Sequence;
                var received = update.Sequence == latestSequence && liveMessage != null ? liveMessage.ReceivedAt : DateTimeOffset.Now;
                var message = new ChatMessage(update.Source, Clean(update.Translation), received, false)
                {
                    Replies = update.Replies,
                    RepliesUnavailable = update.RepliesRequested && update.Replies.Count == 0
                };
                if (update.Overwrite && messages.Count > 0) messages[^1] = message;
                else messages.Add(message);
                while (messages.Count > capacity) messages.RemoveAt(0);
                if (update.Sequence >= latestSequence)
                {
                    latestSequence = update.Sequence;
                    liveMessage = null;
                    status = "실시간 번역";
                    hasError = false;
                }
                break;
            case ChatUpdateKind.Canceled:
            case ChatUpdateKind.Error:
                if (update.Sequence < latestSequence || update.Sequence <= completedSequence) return;
                latestSequence = update.Sequence;
                liveMessage = null;
                hasError = update.Kind == ChatUpdateKind.Error;
                status = hasError ? update.Translation.Replace("[ERROR]", "").Trim() : "음성을 기다리는 중";
                break;
        }
        Changed(nameof(LiveMessage));
        Changed(nameof(IsEmpty));
        Changed(nameof(Status));
        Changed(nameof(HasError));
    }

    /// <summary>Updates pause status and discards provisional text while preserving completed conversation.</summary>
    public void SetPaused(bool paused)
    {
        isPaused = paused;
        liveMessage = null;
        hasError = false;
        status = paused ? "번역 일시정지 · 다시 누르면 재개" : "음성을 기다리는 중";
        Changed(nameof(IsPaused));
        Changed(nameof(LiveMessage));
        Changed(nameof(IsEmpty));
        Changed(nameof(Status));
        Changed(nameof(HasError));
    }

    private static string Clean(string text) => text.Replace("🔤", "").Trim();
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
