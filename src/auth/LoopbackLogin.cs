using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LiveCaptionsTranslator.auth;

/// <summary>Owns a temporary IPv4 loopback listener; accepts only the pending OAuth callback.</summary>
internal sealed class LoopbackLogin : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    internal string RedirectUri { get; }

    internal LoopbackLogin()
    {
        listener.Start();
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/auth/callback";
    }

    /// <summary>Opens the system browser and awaits a verified callback. Cancellation closes all pending reads.</summary>
    internal async Task<(string Code, string ClientId)> RunAsync(OAuthAttempt attempt, string url, CancellationToken token)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var stream = client.GetStream();
            string header;
            try { header = await ReadHeaderAsync(stream, timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
            catch (IOException) { continue; }
            var first = header.Split("\r\n")[0].Split(' ');
            if (first.Length != 3 || first[0] != "GET" || !first[1].StartsWith("/auth/callback?", StringComparison.Ordinal) ||
                !Uri.TryCreate(new Uri(RedirectUri), first[1], out var callback) ||
                callback.GetLeftPart(UriPartial.Path) != RedirectUri || !attempt.MatchesState(callback))
            {
                await ReplyAsync(stream, "400 Bad Request", "This callback does not match a pending sign-in.", token);
                continue;
            }
            try
            {
                var result = attempt.Consume(callback);
                await ReplyAsync(stream, "200 OK", "Sign-in received. Return to LiveCaptions Translator to check the result. You may close this tab.", token);
                return result;
            }
            catch (OAuthException)
            {
                await ReplyAsync(stream, "400 Bad Request", "Sign-in could not be completed. Return to the application.", token);
                throw;
            }
        }
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[16384];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), token);
            if (read == 0) break;
            count += read;
            if (count >= 4 && buffer[count - 4] == 13 && buffer[count - 3] == 10 && buffer[count - 2] == 13 && buffer[count - 1] == 10)
                return Encoding.ASCII.GetString(buffer, 0, count);
        }
        throw new IOException("Invalid callback request.");
    }

    private static async Task ReplyAsync(Stream stream, string status, string message, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(body, token);
        }
        catch (IOException) { /* A closed browser tab does not invalidate a verified callback. */ }
    }

    public void Dispose() => listener.Stop();
}
