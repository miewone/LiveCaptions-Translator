using System.Net.Http;
using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.auth;

/// <summary>Application composition root for the shared OAuth session and isolated HTTP transport.</summary>
internal static class ChatGptRuntime
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    internal static ChatGptSession Session { get; } = new(Http, new WindowsOAuthStore());
    internal static ChatGptResponsesClient Responses { get; } = new(Http, Session, TranslateAPI.Cache);
}
