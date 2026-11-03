namespace LiveCaptionsTranslator.models;

/// <summary>Orders concurrent translations and provisional streams so older captions cannot overwrite newer output.</summary>
public sealed class TranslationTaskQueue
{
    private readonly object sync = new();
    private readonly SemaphoreSlim completionGate = new(1, 1);
    private readonly List<PendingTranslation> tasks = [];
    private long nextSequence;
    private long displayedSequence;
    private (string translatedText, bool isChoke) output = (string.Empty, false);

    /// <summary>Ordered presentation updates. Subscribers should dispatch UI work without blocking the queue.</summary>
    public event Action<ChatUpdate>? CaptionUpdated;

    /// <summary>Cancels pending translations, for example when the user pauses translation.</summary>
    public void CancelAll()
    {
        lock (sync)
            foreach (var pending in tasks.ToArray()) pending.Cancellation.Cancel();
    }

    /// <summary>The latest preview or completed caption; previews never request a display pause.</summary>
    public (string translatedText, bool isChoke) Output { get { lock (sync) return output; } }

    /// <summary>Queues a non-streaming worker for a source caption.</summary>
    public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText) =>
        Enqueue((token, _) => worker(token), originalText);

    /// <summary>Publishes the source before starting a worker with an accumulated-text callback. Only completed results enter translation history.</summary>
    /// <param name="worker">Receives cancellation and an ordered preview callback.</param>
    /// <param name="originalText">Source caption associated with the final history entry.</param>
    public void Enqueue(Func<CancellationToken, Action<string>, Task<(string, bool)>> worker, string originalText)
        => EnqueueResult(async (token, progress) =>
        {
            var result = await worker(token, progress);
            return new TranslationResult(result.Item1, result.Item2);
        }, originalText);

    /// <summary>Queues a translation with optional reply choices, logging only its translated caption.</summary>
    /// <param name="worker">Receives cancellation and a translation-only preview callback.</param>
    /// <param name="originalText">The source paired with this result and its replies.</param>
    public void EnqueueResult(Func<CancellationToken, Action<string>, Task<TranslationResult>> worker, string originalText)
    {
        PendingTranslation pending;
        lock (sync)
        {
            pending = new(++nextSequence, originalText);
            tasks.Add(pending);
            CaptionUpdated?.Invoke(new(pending.Sequence, originalText, string.Empty, ChatUpdateKind.Started));
        }
        _ = RunAsync(pending, worker);
    }

    private void Preview(PendingTranslation pending, string text)
    {
        lock (sync)
        {
            if (pending.Cancellation.IsCancellationRequested || pending.Sequence < displayedSequence) return;
            displayedSequence = pending.Sequence;
            output = (text.Replace("🔤", ""), false);
            CaptionUpdated?.Invoke(new(pending.Sequence, pending.OriginalText, output.translatedText, ChatUpdateKind.Preview));
            foreach (var older in tasks.Where(x => x.Sequence < pending.Sequence).ToArray()) older.Cancellation.Cancel();
        }
    }

    private async Task RunAsync(PendingTranslation pending, Func<CancellationToken, Action<string>, Task<TranslationResult>> worker)
    {
        try
        {
            var result = await worker(pending.Cancellation.Token, text => Preview(pending, text));
            await completionGate.WaitAsync(pending.Cancellation.Token);
            try
            {
                lock (sync)
                {
                    if (pending.Cancellation.IsCancellationRequested || pending.Sequence < displayedSequence) return;
                    displayedSequence = pending.Sequence;
                    output = (result.Text, result.IsChoke);
                    foreach (var older in tasks.Where(x => x.Sequence < pending.Sequence).ToArray()) older.Cancellation.Cancel();
                }
                if (!result.Text.Contains("[ERROR]") && !result.Text.Contains("[WARNING]"))
                {
                    bool overwrite = await Translator.IsOverwrite(pending.OriginalText);
                    if (!overwrite) await Translator.AddContexts();
                    await Translator.Log(pending.OriginalText, result.Text, overwrite);
                    if (!pending.Cancellation.IsCancellationRequested)
                        CaptionUpdated?.Invoke(new(pending.Sequence, pending.OriginalText, result.Text, ChatUpdateKind.Completed, overwrite)
                        { Replies = result.Replies, RepliesRequested = result.RepliesRequested });
                }
                else
                    CaptionUpdated?.Invoke(new(pending.Sequence, pending.OriginalText, result.Text, ChatUpdateKind.Error));
            }
            finally { completionGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            lock (sync)
            {
                if (displayedSequence == pending.Sequence) output = (string.Empty, false);
                CaptionUpdated?.Invoke(new(pending.Sequence, pending.OriginalText, "", ChatUpdateKind.Canceled));
            }
        }
        catch (Exception)
        {
            lock (sync)
                if (pending.Sequence >= displayedSequence)
                {
                    displayedSequence = pending.Sequence;
                    output = ("[ERROR] Translation could not be completed.", false);
                    CaptionUpdated?.Invoke(new(pending.Sequence, pending.OriginalText, output.translatedText, ChatUpdateKind.Error));
                }
        }
        finally
        {
            lock (sync)
            {
                tasks.Remove(pending);
                pending.Cancellation.Dispose();
            }
        }
    }

    /// <summary>Per-request cancellation and sequence identity, independent of asynchronous task start order.</summary>
    private sealed class PendingTranslation(long sequence, string originalText)
    {
        internal long Sequence { get; } = sequence;
        internal string OriginalText { get; } = originalText;
        internal CancellationTokenSource Cancellation { get; } = new();
    }
}
