using System.Text.Json.Serialization;

namespace LiveCaptionsTranslator.auth;

/// <summary>One verified account/client registration and its rotating credential set. Only stored in the protected vault.</summary>
internal sealed class OAuthAccount
{
    public string ClientId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
    [JsonIgnore] public bool CanInvoke => Scopes.Contains("chatgpt.tokens.use.direct") && Scopes.Contains("resource.invoke");
    [JsonIgnore] public string Label => $"{(string.IsNullOrEmpty(Email) ? "ChatGPT account" : Email)} · {ClientId}";

    internal void ClearTokens()
    {
        AccessToken = RefreshToken = IdToken = "";
        Scopes = [];
        ExpiresAt = default;
    }
}

/// <summary>Persisted host identity and isolated account records; contains no application translation settings.</summary>
internal sealed class OAuthVault
{
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public string? ActiveClientId { get; set; }
    public List<OAuthAccount> Accounts { get; set; } = [];
}

/// <summary>Atomic credential persistence with an exclusive cross-process transaction lease.</summary>
internal interface IOAuthStore
{
    Task<IDisposable> LockAsync(CancellationToken token);
    OAuthVault Load();
    void Save(OAuthVault vault);
}
