using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace LiveCaptionsTranslator.auth;

/// <summary>A single PKCE/OIDC transaction. Values belong only to this browser attempt.</summary>
internal sealed class OAuthAttempt
{
    internal const string Issuer = "https://auth.openai.com";
    internal const string Resource = "https://api.openai.com/v1";
    internal const string DynamicClient = "dynamic_agent_client";
    internal const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    internal string State { get; } = RandomValue();
    internal string Nonce { get; } = RandomValue();
    internal string Verifier { get; } = RandomValue();
    internal string RedirectUri { get; }
    internal string? ClientId { get; }
    internal DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddMinutes(5);
    private int consumed;

    /// <summary>Creates an attempt for an already listening loopback URI and optional saved registration.</summary>
    internal OAuthAttempt(string redirectUri, string? clientId)
    {
        var uri = new Uri(redirectUri);
        if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/auth/callback")
            throw new ArgumentException("OAuth requires an IPv4 loopback callback.", nameof(redirectUri));
        RedirectUri = redirectUri;
        ClientId = clientId;
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    private static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Builds the official authorization URL. Never log it: returning sign-ins contain an ID token hint.</summary>
    internal string AuthorizeUrl(string hostId, string? idTokenHint, bool requestConsent)
    {
        var query = HttpUtility.ParseQueryString("");
        query["client_id"] = ClientId ?? DynamicClient;
        if (ClientId == null) query["agent_name_hint"] = "LiveCaptions Translator";
        else if (!string.IsNullOrEmpty(idTokenHint)) query["id_token_hint"] = idTokenHint;
        query["ext_agent_host_id"] = hostId;
        query["response_type"] = "code";
        query["redirect_uri"] = RedirectUri;
        query["scope"] = Scopes;
        query["resource"] = Resource;
        query["state"] = State;
        query["nonce"] = Nonce;
        query["code_challenge_method"] = "S256";
        query["code_challenge"] = Challenge(Verifier);
        if (requestConsent) query["prompt"] = "consent";
        return $"{Issuer}/api/accounts/authorize?{query}";
    }

    internal bool MatchesState(Uri callback)
    {
        var values = HttpUtility.ParseQueryString(callback.Query).GetValues("state");
        return values?.Length == 1 && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(State), Encoding.UTF8.GetBytes(values[0]));
    }

    /// <summary>Consumes a callback once, validating state, exact origin/path and the issued client ID.</summary>
    internal (string Code, string ClientId) Consume(Uri callback)
    {
        if (callback.GetLeftPart(UriPartial.Path) != RedirectUri || !MatchesState(callback) ||
            DateTimeOffset.UtcNow >= ExpiresAt || Interlocked.Exchange(ref consumed, 1) != 0)
            throw new OAuthException("Sign-in could not be verified. Please try again.");
        var query = HttpUtility.ParseQueryString(callback.Query);
        if (query["error"] != null)
            throw new OAuthException("ChatGPT sign-in was declined or could not be completed.");
        var code = query["code"];
        var issued = query["client_id"] ?? ClientId;
        if (query.GetValues("code")?.Length != 1 || string.IsNullOrWhiteSpace(code) ||
            query.GetValues("client_id")?.Length > 1 || string.IsNullOrWhiteSpace(issued) ||
            issued == DynamicClient || (ClientId != null && ClientId != issued))
            throw new OAuthException("The sign-in callback did not contain the expected code and client registration.");
        return (code, issued);
    }
}

/// <summary>A safe, user-facing authentication or inference error; never includes credentials or raw response bodies.</summary>
internal sealed class OAuthException : Exception
{
    internal string? Code { get; }
    internal int? StatusCode { get; }
    internal OAuthException(string message, string? code = null, int? statusCode = null) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}
