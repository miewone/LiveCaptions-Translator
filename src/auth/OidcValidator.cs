using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.auth;

/// <summary>Validates OpenAI RS256 ID tokens using trusted discovery metadata and cached signing keys.</summary>
internal sealed class OidcValidator(HttpClient http)
{
    private JsonElement keys;
    private DateTimeOffset keysExpire;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<Uri> DiscoverEndpointAsync(string name, CancellationToken token)
    {
        using var response = await http.GetAsync(OAuthAttempt.Issuer + "/.well-known/openid-configuration", token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (doc.RootElement.GetProperty("issuer").GetString() != OAuthAttempt.Issuer)
            throw new OAuthException("Unexpected OpenAI identity issuer.");
        var uri = new Uri(doc.RootElement.GetProperty(name).GetString()!);
        if (uri.Scheme != "https" || uri.Host != "auth.openai.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            throw new OAuthException("Unexpected OpenAI identity endpoint.");
        return uri;
    }

    /// <summary>Checks signature and OIDC claims before returning identity. Nonce is required for browser sign-in.</summary>
    internal async Task<(string Subject, string Email)> ValidateAsync(
        string jwt, string clientId, string? nonce, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3 || jwt.Length > 65536) throw new OAuthException("Invalid ID token.");
            using var header = JsonDocument.Parse(OAuthAttempt.Decode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256")
                throw new OAuthException("Unsupported ID token signature algorithm.");
            var kid = header.RootElement.GetProperty("kid").GetString();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (keys.ValueKind == JsonValueKind.Undefined || keysExpire <= DateTimeOffset.UtcNow || attempt > 0)
                {
                    var endpoint = await DiscoverEndpointAsync("jwks_uri", token);
                    using var response = await http.GetAsync(endpoint, token);
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    keys = doc.RootElement.GetProperty("keys").Clone();
                    keysExpire = DateTimeOffset.UtcNow.AddHours(1);
                }
                foreach (var key in keys.EnumerateArray())
                {
                    if (key.GetProperty("kid").GetString() != kid || key.GetProperty("kty").GetString() != "RSA") continue;
                    if (key.TryGetProperty("use", out var use) && use.GetString() != "sig") continue;
                    if (key.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256") continue;
                    using var rsa = RSA.Create();
                    rsa.ImportParameters(new RSAParameters
                    {
                        Modulus = OAuthAttempt.Decode(key.GetProperty("n").GetString()!),
                        Exponent = OAuthAttempt.Decode(key.GetProperty("e").GetString()!)
                    });
                    if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
                        OAuthAttempt.Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                        throw new OAuthException("The ID token signature could not be verified.");
                    using var claims = JsonDocument.Parse(OAuthAttempt.Decode(parts[1]));
                    return ValidateClaims(claims.RootElement, clientId, nonce);
                }
            }
            throw new OAuthException("No trusted signing key matched the ID token.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or CryptographicException)
        {
            throw new OAuthException("The ID token could not be verified.");
        }
        finally { gate.Release(); }
    }

    private static (string, string) ValidateClaims(JsonElement claims, string clientId, string? nonce)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var aud = claims.GetProperty("aud");
        var audiences = aud.ValueKind == JsonValueKind.Array
            ? aud.EnumerateArray().Select(x => x.GetString()).ToArray() : [aud.GetString()];
        var hasAzp = claims.TryGetProperty("azp", out var azp);
        if (claims.GetProperty("iss").GetString() != OAuthAttempt.Issuer || !audiences.Contains(clientId) ||
            (audiences.Length > 1 && !hasAzp) || (hasAzp && azp.GetString() != clientId) ||
            claims.GetProperty("exp").GetInt64() <= now - 5 || claims.GetProperty("iat").GetInt64() > now + 5 ||
            (claims.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now + 5) ||
            (nonce != null && (!claims.TryGetProperty("nonce", out var n) || n.GetString() != nonce)))
            throw new OAuthException("The ID token identity, lifetime, or nonce did not match.");
        var subject = claims.GetProperty("sub").GetString();
        if (string.IsNullOrWhiteSpace(subject)) throw new OAuthException("The ID token has no account identity.");
        return (subject, claims.TryGetProperty("email", out var email) ? email.GetString() ?? "" : "");
    }
}
