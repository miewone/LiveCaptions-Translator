using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LiveCaptionsTranslator.auth;

/// <summary>Coordinates OAuth sign-in, account isolation, refresh rotation, revocation and inference sessions.</summary>
internal sealed class ChatGptSession
{
    private readonly HttpClient http;
    private readonly IOAuthStore store;
    private readonly OidcValidator validator;
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private readonly object sessionLock = new();
    private CancellationTokenSource sessionCancellation = new();
    private readonly Dictionary<string, DateTimeOffset> cooldowns = [];
    private readonly HashSet<string> pausedAccounts = [];

    internal ChatGptSession(HttpClient http, IOAuthStore store)
    {
        this.http = http;
        this.store = store;
        validator = new OidcValidator(http);
    }

    internal CancellationToken SessionToken { get { lock (sessionLock) return sessionCancellation.Token; } }

    internal async Task<OAuthVault> ReadAsync(CancellationToken token)
    {
        using var lease = await store.LockAsync(token);
        return store.Load();
    }

    /// <summary>Authenticates a new or saved client in the browser and commits only a verified identity.</summary>
    internal async Task SignInAsync(string? clientId, bool requestConsent, CancellationToken token)
    {
        await loginGate.WaitAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            OAuthVault vault;
            using (await store.LockAsync(timeout.Token))
            {
                vault = store.Load();
                store.Save(vault); // Persist host identity before the first authorization.
            }
            var previous = clientId == null ? null : vault.Accounts.Single(x => x.ClientId == clientId);
            using var listener = new LoopbackLogin();
            var attempt = new OAuthAttempt(listener.RedirectUri, previous?.ClientId);
            var callback = await listener.RunAsync(attempt,
                attempt.AuthorizeUrl(vault.HostId, previous?.IdToken, requestConsent), timeout.Token);
            using var response = await TokenRequestAsync(new()
            {
                ["grant_type"] = "authorization_code", ["client_id"] = callback.ClientId,
                ["code"] = callback.Code, ["code_verifier"] = attempt.Verifier,
                ["redirect_uri"] = attempt.RedirectUri, ["resource"] = OAuthAttempt.Resource
            }, timeout.Token);
            var root = response.RootElement;
            var idToken = root.GetProperty("id_token").GetString()!;
            var identity = await validator.ValidateAsync(idToken, callback.ClientId, attempt.Nonce, timeout.Token);
            if (previous != null && previous.Subject != identity.Subject)
                throw new OAuthException("This sign-in belongs to a different ChatGPT account.");
            var account = new OAuthAccount { ClientId = callback.ClientId, Subject = identity.Subject, Email = identity.Email, IdToken = idToken };
            ApplyTokens(account, root);
            using (await store.LockAsync(timeout.Token))
            {
                vault = store.Load();
                var existing = vault.Accounts.Find(x => x.ClientId == account.ClientId);
                if (existing != null && existing.Subject != account.Subject)
                    throw new OAuthException("This client registration belongs to a different account.");
                vault.Accounts.RemoveAll(x => x.ClientId == account.ClientId);
                vault.Accounts.Add(account);
                vault.ActiveClientId = account.ClientId;
                store.Save(vault);
                ResetSession(account.ClientId);
            }
        }
        finally { loginGate.Release(); }
    }

    /// <summary>Selects a saved registration and cancels requests belonging to the previous selection.</summary>
    internal async Task SelectAsync(string clientId, CancellationToken token)
    {
        using var lease = await store.LockAsync(token);
        var vault = store.Load();
        if (!vault.Accounts.Any(x => x.ClientId == clientId)) throw new OAuthException("Choose a saved ChatGPT account.");
        vault.ActiveClientId = clientId;
        store.Save(vault);
        ResetSession(null);
    }

    internal void Resume(string clientId) => ResetSession(clientId);

    private void ResetSession(string? resumeClient)
    {
        lock (sessionLock)
        {
            sessionCancellation.Cancel();
            sessionCancellation.Dispose();
            sessionCancellation = new();
            if (resumeClient != null) { pausedAccounts.Remove(resumeClient); cooldowns.Remove(resumeClient); }
        }
    }

    /// <summary>Revokes the selected session and clears local tokens even if the remote endpoint is unavailable.</summary>
    internal async Task<bool> SignOutAsync(string clientId, CancellationToken token)
    {
        using var lease = await store.LockAsync(token);
        var vault = store.Load();
        var account = vault.Accounts.Single(x => x.ClientId == clientId);
        ResetSession(null);
        var revoked = string.IsNullOrEmpty(account.RefreshToken);
        try
        {
            if (!revoked)
            {
                var endpoint = await validator.DiscoverEndpointAsync("revocation_endpoint", token);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["token"] = account.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId
                        });
                        using var response = await http.PostAsync(endpoint, form, token);
                        revoked = response.StatusCode == HttpStatusCode.OK;
                        if (revoked || (int)response.StatusCode < 500) break;
                    }
                    catch (HttpRequestException) when (attempt == 0) { }
                    if (attempt == 0) await Task.Delay(500, token);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or OAuthException or JsonException) { }
        finally
        {
            account.ClearTokens();
            store.Save(vault);
        }
        return revoked;
    }

    /// <summary>Returns a usable token, serializing refreshes across processes and preserving tokens on transient errors.</summary>
    internal async Task<(string ClientId, string AccessToken)> GetAccessAsync(CancellationToken token)
    {
        using var lease = await store.LockAsync(token);
        var vault = store.Load();
        var account = vault.Accounts.Find(x => x.ClientId == vault.ActiveClientId);
        if (account == null || string.IsNullOrEmpty(account.AccessToken))
            throw new OAuthException("Open API Settings → ChatGPT OAuth and continue with ChatGPT.");
        lock (sessionLock)
        {
            if (pausedAccounts.Contains(account.ClientId))
                throw new OAuthException("ChatGPT requests are paused. Check Usage, then choose Resume requests in API Settings.");
            if (cooldowns.TryGetValue(account.ClientId, out var until) && until > DateTimeOffset.UtcNow)
                throw new OAuthException("ChatGPT is temporarily unavailable. Waiting before the next request.");
        }
        if (!account.CanInvoke) throw new OAuthException("ChatGPT plan use is disabled. Choose Enable plan usage in API Settings.");
        if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(60))
        {
            if (string.IsNullOrEmpty(account.RefreshToken)) throw new OAuthException("Sign in to ChatGPT again to renew access.");
            try
            {
                using var response = await TokenRequestAsync(new()
                {
                    ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId,
                    ["refresh_token"] = account.RefreshToken, ["resource"] = OAuthAttempt.Resource
                }, token);
                var root = response.RootElement;
                if (root.TryGetProperty("id_token", out var refreshedId))
                {
                    var identity = await validator.ValidateAsync(refreshedId.GetString()!, account.ClientId, null, token);
                    if (identity.Subject != account.Subject) throw new OAuthException("Refreshed identity did not match the selected account.");
                    account.IdToken = refreshedId.GetString()!;
                }
                ApplyTokens(account, root, refresh: true);
                store.Save(vault);
            }
            catch (OAuthException ex) when (ex.Code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or
                "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused")
            {
                account.ClearTokens();
                store.Save(vault);
                throw new OAuthException("The ChatGPT session has expired or was revoked. Sign in again.");
            }
            catch (Exception ex) when (ex is HttpRequestException or OAuthException)
            {
                BackOff(account.ClientId, TimeSpan.FromSeconds(30));
                throw;
            }
        }
        if (!account.CanInvoke) throw new OAuthException("ChatGPT plan use is no longer authorized. Enable plan usage in API Settings.");
        return (account.ClientId, account.AccessToken);
    }

    private async Task<JsonDocument> TokenRequestAsync(Dictionary<string, string> fields, CancellationToken token)
    {
        using var form = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(OAuthAttempt.Issuer + "/api/accounts/oauth/token", form, token);
        if (!response.IsSuccessStatusCode) throw await ReadErrorAsync(response, token);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    internal static void ApplyTokens(OAuthAccount account, JsonElement root, bool refresh = false)
    {
        var access = root.GetProperty("access_token").GetString();
        var type = root.GetProperty("token_type").GetString();
        var expiry = root.GetProperty("expires_in").GetInt32();
        var rotating = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var scopes = root.TryGetProperty("scope", out var scope) ? (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : refresh ? account.Scopes : [];
        if (string.IsNullOrWhiteSpace(access) || !string.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            expiry <= 0 || (refresh && string.IsNullOrWhiteSpace(rotating)))
            throw new OAuthException("OpenAI returned an incomplete credential set. Sign in again.");
        account.AccessToken = access;
        account.RefreshToken = rotating ?? "";
        account.Scopes = scopes;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiry);
    }

    internal void BackOff(string clientId, TimeSpan delay)
    {
        lock (sessionLock) cooldowns[clientId] = DateTimeOffset.UtcNow.Add(delay);
    }

    internal void HandleInferenceError(string clientId, OAuthException error)
    {
        lock (sessionLock)
        {
            if (error.StatusCode is 400 or 401 or 403 or 429 || error.Code is "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_user_not_eligible" or "401" or "403" or "429")
                pausedAccounts.Add(clientId);
            else cooldowns[clientId] = DateTimeOffset.UtcNow.AddSeconds(30);
        }
    }

    internal static HttpRequestMessage Authorized(HttpMethod method, string endpoint, string accessToken)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    internal static async Task<OAuthException> ReadErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        string? code = null;
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (body.RootElement.TryGetProperty("error", out var error))
                code = error.ValueKind == JsonValueKind.String ? error.GetString()
                    : error.TryGetProperty("code", out var c) ? c.GetString() : null;
        }
        catch (JsonException) { }
        // Do not echo a remote body or arbitrary diagnostic string into caption logs.
        code = SafeCode(code) ?? ((int)response.StatusCode).ToString();
        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? SafeDiagnosticId(ids.FirstOrDefault())
            : response.Headers.TryGetValues("openai-request-id", out var openaiIds) ? SafeDiagnosticId(openaiIds.FirstOrDefault()) : null;
        var diagnostic = requestId == null ? "" : $" Request ID: {requestId}.";
        return new OAuthException($"ChatGPT request failed (HTTP {(int)response.StatusCode}, {code}). Check sign-in, plan access, or ChatGPT Usage.{diagnostic}", code, (int)response.StatusCode);
    }

    private static string? SafeDiagnosticId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : null;

    internal static string? SafeCode(string? code) => code is { Length: > 0 and <= 100 } &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : null;
}
