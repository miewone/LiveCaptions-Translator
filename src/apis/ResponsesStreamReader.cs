using System.IO;
using System.Text;
using System.Text.Json;
using LiveCaptionsTranslator.auth;

namespace LiveCaptionsTranslator.apis;

/// <summary>Consumes Responses SSE events, emitting previews but accepting only a completed response as a result.</summary>
internal sealed class ResponsesStreamReader
{
    /// <summary>Reads a UTF-8 SSE stream. The progress callback receives accumulated text; cancellation interrupts reads.</summary>
    internal async Task<string> ReadAsync(Stream stream, Action<string>? progress, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var data = new StringBuilder();
        var text = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(token);
            if (line == null)
                throw new OAuthException("ChatGPT disconnected before completing the translation. Please try again.");
            if (line.Length > 1_048_576 || data.Length > 1_048_576 || text.Length > 262144)
                throw new OAuthException("ChatGPT returned an unexpectedly large translation.");
            if (line.Length > 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
                }
                continue;
            }
            if (data.Length == 0) continue;
            var json = data.ToString();
            data.Clear();
            if (json == "[DONE]") throw new OAuthException("ChatGPT did not confirm response completion.");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "response.output_text.delta":
                    text.Append(root.GetProperty("delta").GetString());
                    progress?.Invoke(text.ToString());
                    break;
                case "response.refusal.delta":
                case "response.refusal.done":
                    throw new OAuthException("ChatGPT declined this translation.");
                case "error":
                case "response.failed":
                case "response.incomplete":
                    var error = root.TryGetProperty("response", out var failed) ? failed : root;
                    if (error.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.Object) error = detail;
                    var code = error.TryGetProperty("code", out var c) ? ChatGptSession.SafeCode(c.GetString()) : null;
                    throw new OAuthException($"ChatGPT translation did not complete ({code ?? type}). Check ChatGPT Usage or try again later.", code);
                case "response.completed":
                    var response = root.GetProperty("response");
                    if (!response.TryGetProperty("status", out var status) || status.GetString() != "completed")
                        throw new OAuthException("ChatGPT did not confirm a completed response.");
                    // The terminal output is authoritative, even when a proxy coalesces delta events.
                    var final = new StringBuilder();
                    if (response.TryGetProperty("output", out var output))
                        foreach (var item in output.EnumerateArray())
                            if (item.TryGetProperty("content", out var content))
                                foreach (var part in content.EnumerateArray())
                                {
                                    if (part.GetProperty("type").GetString() == "refusal")
                                        throw new OAuthException("ChatGPT declined this translation.");
                                    if (part.GetProperty("type").GetString() == "output_text") final.Append(part.GetProperty("text").GetString());
                                }
                    var result = final.Length > 0 ? final.ToString() : text.ToString();
                    if (string.IsNullOrWhiteSpace(result)) throw new OAuthException("ChatGPT returned no translation text.");
                    return result;
            }
        }
    }
}
