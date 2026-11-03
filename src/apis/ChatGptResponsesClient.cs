using System.Net.Http;
using System.Text;
using System.Text.Json;
using LiveCaptionsTranslator.auth;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.apis;

/// <summary>Calls the public Responses API with ChatGPT plan credentials; no API-key billing fallback.</summary>
internal sealed class ChatGptResponsesClient(HttpClient http, ChatGptSession session, TranslationCache? translationCache = null)
{
    private readonly TranslationCache cache = translationCache ?? new();
    private readonly object cacheScopeLock = new();
    private CancellationToken cacheSessionToken;
    private string cacheScope = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim inferenceGate = new(2, 2);
    private long latestRequest;

    /// <summary>Restores an account catalog from settings or fetches it once; explicit refresh replaces it only on success.</summary>
    /// <param name="config">Settings owning the per-account catalog.</param>
    /// <param name="clientId">Currently selected client registration.</param>
    /// <param name="refresh">True only for an explicit catalog refresh.</param>
    /// <param name="token">Cancellation for the settings operation.</param>
    internal async Task<IReadOnlyList<ChatGptModel>> GetModelsAsync(ChatGptOAuthConfig config, string clientId,
        bool refresh, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (config.CatalogAccountId != clientId) throw new OperationCanceledException("The selected account changed.");
        if (!refresh && config.HasCachedCatalog) return config.CachedModels;
        var sessionToken = session.SessionToken;
        var models = await ListModelsAsync(token);
        token.ThrowIfCancellationRequested();
        sessionToken.ThrowIfCancellationRequested();
        if (config.CatalogAccountId != clientId) throw new OperationCanceledException("The selected account changed.");
        config.StoreCatalog(clientId, models);
        return config.CachedModels;
    }

    /// <summary>Fetches the selected account's visible models, preserving server order.</summary>
    internal async Task<List<ChatGptModel>> ListModelsAsync(CancellationToken token)
    {
        var sessionToken = session.SessionToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var credentials = await session.GetAccessAsync(timeout.Token);
        using var request = ChatGptSession.Authorized(HttpMethod.Get, OAuthAttempt.Resource + "/models", credentials.AccessToken);
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ChatGptSession.ReadErrorAsync(response, timeout.Token);
            session.HandleInferenceError(credentials.ClientId, error);
            throw error;
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return doc.RootElement.GetProperty("models").EnumerateArray()
            .Where(x => x.TryGetProperty("visibility", out var v) && v.GetString() == "list")
            .Select(x => new ChatGptModel(x.GetProperty("slug").GetString()!, x.GetProperty("display_name").GetString()!)).ToList();
    }

    /// <summary>Streams a translation using instructions and user/assistant context, omitting unsupported sampling parameters.</summary>
    internal async Task<string> TranslateAsync(string model, string instructions, object input, Action<string>? progress, CancellationToken token,
        string reasoningEffort = "default", JsonElement? responseFormat = null)
    {
        var sequence = Interlocked.Increment(ref latestRequest);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, session.SessionToken);
        await inferenceGate.WaitAsync(pending.Token);
        try
        {
            if (sequence != Volatile.Read(ref latestRequest)) throw new OperationCanceledException("Superseded caption.");
            return await TranslateCoreAsync(model, instructions, input, progress, pending.Token, reasoningEffort, responseFormat);
        }
        finally { inferenceGate.Release(); }
    }

    /// <summary>Uses one model request for a caption and, optionally, three bilingual reply choices.</summary>
    /// <param name="model">Selected account model.</param>
    /// <param name="instructions">Translation instructions with the target language already substituted.</param>
    /// <param name="input">Current source caption and optional prior context.</param>
    /// <param name="includeReplies">Whether to request the WARDOGS JSON response format.</param>
    /// <param name="progress">Receives translation text only, never raw JSON or reply text.</param>
    /// <param name="token">Cancellation for superseded or paused captions.</param>
    /// <param name="reasoningEffort">This model's saved reasoning override, or default to use the server default.</param>
    internal async Task<TranslationResult> TranslateCaptionAsync(string model, string instructions, object input,
        bool includeReplies, Action<string>? progress, CancellationToken token, string reasoningEffort = "default")
    {
        var sessionToken = session.SessionToken;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
        pending.Token.ThrowIfCancellationRequested();
        string scope;
        lock (cacheScopeLock)
        {
            if (cacheSessionToken != sessionToken)
            {
                cacheSessionToken = sessionToken;
                cacheScope = Guid.NewGuid().ToString("N");
            }
            scope = cacheScope;
        }
        var key = TranslationCache.CreateKey(new { engine = "ChatGptOAuth", scope, model, instructions, input, includeReplies, reasoningEffort });
        if (cache.TryGet(key, out var cached))
        {
            pending.Token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref latestRequest);
            progress?.Invoke(cached!.Text);
            pending.Token.ThrowIfCancellationRequested();
            return cached;
        }

        var result = await GenerateCaptionAsync(model, instructions, input, includeReplies, progress, pending.Token, reasoningEffort);
        pending.Token.ThrowIfCancellationRequested();
        cache.Store(key, result);
        return result;
    }

    private async Task<TranslationResult> GenerateCaptionAsync(string model, string instructions, object input,
        bool includeReplies, Action<string>? progress, CancellationToken token, string reasoningEffort)
    {
        if (!includeReplies)
            return new(await TranslateAsync(model, instructions, input, progress, token, reasoningEffort));

        string? lastPreview = null;
        var json = await TranslateAsync(model, instructions + "\n" + WardogsCaptionResponse.Instructions, input, text =>
        {
            var preview = WardogsCaptionResponse.ReadTranslationPreview(text, allowPartial: true);
            if (!string.IsNullOrWhiteSpace(preview) && preview != lastPreview)
            {
                lastPreview = preview;
                progress?.Invoke(preview);
            }
        }, token, reasoningEffort, WardogsCaptionResponse.ResponseFormat);
        return WardogsCaptionResponse.Parse(json);
    }

    private async Task<string> TranslateCoreAsync(string model, string instructions, object input, Action<string>? progress,
        CancellationToken token, string reasoningEffort, JsonElement? responseFormat)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new OAuthException("Load and select a ChatGPT model in API Settings first.");
        if (!ChatGptReasoning.IsAllowed(model, reasoningEffort))
            throw new OAuthException("선택한 모델의 추론 강도를 확인해 주세요.");
        var sessionToken = session.SessionToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        string? clientId = null;
        try
        {
            var credentials = await session.GetAccessAsync(timeout.Token);
            clientId = credentials.ClientId;
            using var request = ChatGptSession.Authorized(HttpMethod.Post, OAuthAttempt.Resource + "/responses", credentials.AccessToken);
            request.Headers.Accept.ParseAdd("text/event-stream");
            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["instructions"] = instructions,
                ["input"] = input,
                ["store"] = false,
                ["stream"] = true
            };
            if (reasoningEffort != "default")
                body["reasoning"] = new { effort = reasoningEffort };
            if (responseFormat.HasValue)
                body["text"] = new { format = responseFormat.Value };
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw await ChatGptSession.ReadErrorAsync(response, timeout.Token);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await new ResponsesStreamReader().ReadAsync(stream, progress, timeout.Token);
        }
        catch (OAuthException ex)
        {
            if (clientId != null) session.HandleInferenceError(clientId, ex);
            throw;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !sessionToken.IsCancellationRequested)
        {
            if (clientId != null) session.BackOff(clientId, TimeSpan.FromSeconds(15));
            throw new OAuthException("ChatGPT translation timed out. Please check your connection.");
        }
        catch (HttpRequestException)
        {
            if (clientId != null) session.BackOff(clientId, TimeSpan.FromSeconds(15));
            throw new OAuthException("Unable to connect to ChatGPT. Check your network connection.");
        }
    }
}
