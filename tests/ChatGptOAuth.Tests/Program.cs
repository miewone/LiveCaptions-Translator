using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveCaptionsTranslator;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.auth;
using LiveCaptionsTranslator.models;

var tests = new (string Name, Func<Task> Run)[]
{
    ("PKCE RFC 7636 challenge", () => Check(() => Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthAttempt.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")))),
    ("dynamic registration, host and requested scopes", () => Check(() =>
    {
        var a = new OAuthAttempt("http://127.0.0.1:12345/auth/callback", null);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(a.AuthorizeUrl("urn:uuid:host", null, false)).Query);
        Equal("dynamic_agent_client", query["client_id"]!);
        Equal("urn:uuid:host", query["ext_agent_host_id"]!);
        Equal("S256", query["code_challenge_method"]!);
        True(query["scope"]!.Contains("chatgpt.tokens.use.direct"));
        var result = a.Consume(new Uri(a.RedirectUri + "?state=" + a.State + "&code=test&client_id=oaiapp_test"));
        Equal("oaiapp_test", result.ClientId);
        Throws<OAuthException>(() => a.Consume(new Uri(a.RedirectUri + "?state=" + a.State + "&code=test&client_id=oaiapp_test")));
    })),
    ("callback rejects state, client substitution, wrong path and duplicate parameters", () => Check(() =>
    {
        foreach (var suffix in new[] { "?state=wrong&code=a", "?state={state}&code=a&client_id=other", "?state={state}&code=a&code=b", "?state={state}&state={state}&code=a" })
        {
            var a = new OAuthAttempt("http://127.0.0.1:12345/auth/callback", "saved");
            Throws<OAuthException>(() => a.Consume(new Uri(a.RedirectUri + suffix.Replace("{state}", a.State))));
        }
        var b = new OAuthAttempt("http://127.0.0.1:12345/auth/callback", null);
        Throws<OAuthException>(() => b.Consume(new Uri(b.RedirectUri + "?state=" + b.State + "&code=a")));
        Throws<OAuthException>(() => b.Consume(new Uri("http://127.0.0.1:12345/other?state=" + b.State + "&code=a&client_id=issued")));
    })),
    ("returning sign-in uses issued client and no registration hint", () => Check(() =>
    {
        var a = new OAuthAttempt("http://127.0.0.1:12345/auth/callback", "saved");
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(a.AuthorizeUrl("host", "retained-id", true)).Query);
        Equal("saved", query["client_id"]!);
        True(query["agent_name_hint"] == null);
        Equal("consent", query["prompt"]!);
        Equal("saved", a.Consume(new Uri(a.RedirectUri + "?state=" + a.State + "&code=a")).ClientId);
    })),
    ("valid RS256 token and cached JWKS", ValidIdentity),
    ("reject invalid signature and OIDC claims", InvalidIdentities),
    ("refresh rotation is serialized and persisted", ConcurrentRefresh),
    ("terminal refresh failure clears credentials only", TerminalRefresh),
    ("transient refresh failure preserves credentials and backs off", TransientRefresh),
    ("missing plan scope prevents inference", MissingScope),
    ("logout cancels session and preserves registration", Logout),
    ("SSE deltas and terminal output", StreamSuccess),
    ("SSE failures, refusal, truncation and DONE are not success", StreamFailures),
    ("SSE multiline framing and cancellation", StreamFraming),
    ("Responses contract and selected model catalog", ResponsesContract),
    ("429 pauses new requests until explicit resume", UsagePause),
    ("streamed usage failure also pauses requests", StreamUsagePause),
    ("account switch cancels in-flight inference", SwitchCancellation),
    ("bounded inference skips stale waiting captions", BoundedInference),
    ("HTTP diagnostics are safe and retain request IDs", SafeDiagnostics),
    ("queue rejects stale previews and logs only final text", OrderedQueue),
    ("queue cancellation clears preview without logging", CanceledQueue),
    ("chat previews update one paired row then finalize", ChatPreview),
    ("chat rejects stale events without losing newer preview", ChatOrdering),
    ("chat final revisions replace the last row and history stays bounded", ChatHistory),
    ("chat pause and errors preserve completed history", ChatPauseAndError),
    ("queue emits paired chat updates and observes pause cancellation", ChatQueueEvents),
    ("queue shows source before a non-streaming translation starts", ChatSourceFirst),
    ("combined JSON streams decoded translation before replies", CombinedJsonPreview),
    ("invalid reply data preserves translation without leaking JSON", CombinedJsonValidation),
    ("WARDOGS translation and replies use one HTTP request", CombinedRequest),
    ("ordinary translation keeps its original request and streaming format", OrdinaryCaptionRequest),
    ("reply choices stay paired and never enter translation history", ReplyQueueOrdering),
    ("model-specific reasoning values reach the request unchanged", ReasoningRequestSettings),
    ("model reasoning preferences and account selections survive restart", ModelPreferences),
    ("legacy speed setting migrates without replacing explicit preferences", ReasoningMigration),
    ("model catalogs are reused until explicit refresh and survive failed refresh", CachedModelCatalog),
    ("partial JSON previews decode text without leaking escapes", PartialJsonPreview),
    ("translation cache expires absolutely after fifteen minutes", TranslationCacheExpiry),
    ("translation cache evicts least recently used entries within its byte budget", TranslationCacheBudget),
    ("translation cache snapshots results and excludes errors or incomplete replies", TranslationCacheValidation),
    ("OAuth caption cache reuses replies and isolates all request settings", CaptionCacheRequests),
    ("failed and canceled captions never populate the cache", CaptionCacheFailure),
    ("translation cache remains bounded under concurrent access", TranslationCacheConcurrency),
    ("combined captions tolerate JSON wrappers and property casing", WrappedCaptionJson),
};
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static Task Check(Action action) { action(); return Task.CompletedTask; }
static void True(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
static string Complete(string text) => Event(new { type = "response.completed", response = new { status = "completed", output = new[] { new { content = new[] { new { type = "output_text", text } } } } } });
static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
static string Tokens() => """{"access_token":"new-access","refresh_token":"new-refresh","token_type":"Bearer","expires_in":3600,"scope":"openid resource.invoke chatgpt.tokens.use.direct"}""";
static Dictionary<string, object> Claims() => new()
{
    ["iss"] = OAuthAttempt.Issuer,
    ["sub"] = "subject",
    ["aud"] = "client",
    ["nonce"] = "nonce",
    ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
    ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
};
static string Jwt(RSA rsa, Dictionary<string, object> claims, string algorithm = "RS256")
{
    var header = OAuthAttempt.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = algorithm, kid = "test-key" }));
    var payload = OAuthAttempt.Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
    return header + "." + payload + "." + OAuthAttempt.Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
}
static HttpClient IdentityHttp(RSA rsa, Action? jwksRequest = null)
{
    var key = rsa.ExportParameters(false);
    return new(new StubHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration"))
            return Task.FromResult(Json("""{"issuer":"https://auth.openai.com","jwks_uri":"https://auth.openai.com/.well-known/jwks.json"}"""));
        jwksRequest?.Invoke();
        return Task.FromResult(Json(JsonSerializer.Serialize(new { keys = new[] { new { kid = "test-key", kty = "RSA", use = "sig", alg = "RS256", n = OAuthAttempt.Base64Url(key.Modulus!), e = OAuthAttempt.Base64Url(key.Exponent!) } } })));
    }));
}
static async Task ValidIdentity()
{
    using var rsa = RSA.Create(2048);
    var requests = 0;
    using var http = IdentityHttp(rsa, () => requests++);
    var validator = new OidcValidator(http);
    var jwt = Jwt(rsa, Claims());
    Equal("subject", (await validator.ValidateAsync(jwt, "client", "nonce", default)).Subject);
    await validator.ValidateAsync(jwt, "client", "nonce", default);
    Equal(1, requests);
}
static async Task InvalidIdentities()
{
    using var rsa = RSA.Create(2048);
    using var bad = RSA.Create(2048);
    using var http = IdentityHttp(rsa);
    var validator = new OidcValidator(http);
    await ThrowsAsync<OAuthException>(() => validator.ValidateAsync(Jwt(bad, Claims()), "client", "nonce", default));
    await ThrowsAsync<OAuthException>(() => validator.ValidateAsync(Jwt(rsa, Claims(), "none"), "client", "nonce", default));
    foreach (var change in new (string Key, object Value)[] { ("iss", "https://evil.example"), ("aud", "other"), ("nonce", "wrong"), ("sub", ""), ("exp", 0L), ("iat", long.MaxValue), ("azp", "other"), ("aud", new[] { "client", "other" }) })
    {
        var claims = Claims(); claims[change.Key] = change.Value;
        await ThrowsAsync<OAuthException>(() => validator.ValidateAsync(Jwt(rsa, claims), "client", "nonce", default));
    }
}
static async Task ConcurrentRefresh()
{
    var store = new MemoryStore(expired: true);
    var calls = 0;
    using var http = new HttpClient(new StubHandler(async request =>
    {
        calls++;
        var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync());
        Equal("refresh_token", form["grant_type"]!); Equal("client", form["client_id"]!);
        Equal("old-refresh", form["refresh_token"]!); True(form["scope"] == null);
        Equal(OAuthAttempt.Resource, form["resource"]!);
        await Task.Delay(20);
        return Json(Tokens());
    }));
    var session = new ChatGptSession(http, store);
    var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => session.GetAccessAsync(default)));
    True(results.All(x => x.AccessToken == "new-access")); Equal(1, calls);
    Equal("new-refresh", store.Load().Accounts[0].RefreshToken);
    Equal("client", store.Load().Accounts[0].ClientId);
}
static async Task TerminalRefresh()
{
    var store = new MemoryStore(true);
    using var http = new HttpClient(new StubHandler(_ => Task.FromResult(Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest))));
    var session = new ChatGptSession(http, store);
    var host = store.Load().HostId;
    await ThrowsAsync<OAuthException>(() => session.GetAccessAsync(default));
    var saved = store.Load(); Equal("", saved.Accounts[0].AccessToken); Equal("client", saved.Accounts[0].ClientId); Equal(host, saved.HostId);
}
static async Task TransientRefresh()
{
    var store = new MemoryStore(true);
    var calls = 0;
    using var http = new HttpClient(new StubHandler(_ => { calls++; return Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable)); }));
    var session = new ChatGptSession(http, store);
    await ThrowsAsync<OAuthException>(() => session.GetAccessAsync(default));
    await ThrowsAsync<OAuthException>(() => session.GetAccessAsync(default));
    Equal(1, calls); Equal("old-refresh", store.Load().Accounts[0].RefreshToken);
}
static async Task MissingScope()
{
    var store = new MemoryStore(); var data = store.Load(); data.Accounts[0].Scopes = ["openid"]; store.Save(data);
    using var http = new HttpClient(new StubHandler(_ => throw new Exception("No network request expected")));
    await ThrowsAsync<OAuthException>(() => new ChatGptSession(http, store).GetAccessAsync(default));
}
static async Task Logout()
{
    var store = new MemoryStore(); var requests = 0;
    using var http = new HttpClient(new StubHandler(async request =>
    {
        if (request.Method == HttpMethod.Get) return Json("""{"issuer":"https://auth.openai.com","revocation_endpoint":"https://auth.openai.com/api/accounts/oauth/revoke"}""");
        var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync());
        Equal("old-refresh", form["token"]!); Equal("client", form["client_id"]!); requests++;
        return new(HttpStatusCode.OK);
    }));
    var session = new ChatGptSession(http, store); var old = session.SessionToken;
    True(await session.SignOutAsync("client", default)); True(old.IsCancellationRequested); Equal(1, requests);
    Equal("", store.Load().Accounts[0].RefreshToken); Equal("subject", store.Load().Accounts[0].Subject);
}
static async Task StreamSuccess()
{
    var previews = new List<string>();
    using var stream = Stream(Event(new { type = "response.output_text.delta", delta = "안녕" }) + Event(new { type = "response.output_text.delta", delta = "하세요" }) + Complete("안녕하세요"));
    Equal("안녕하세요", await new ResponsesStreamReader().ReadAsync(stream, previews.Add, default));
    Equal(2, previews.Count); Equal("안녕하세요", previews[1]);
}
static async Task StreamFailures()
{
    foreach (var suffix in new[] { "", "data: [DONE]\n\n", Event(new { type = "response.failed", response = new { error = new { code = "subscription_sharing_usage_limit_exceeded" } } }), Event(new { type = "response.incomplete" }), Event(new { type = "response.refusal.done" }) })
    {
        using var stream = Stream(Event(new { type = "response.output_text.delta", delta = "partial" }) + suffix);
        await ThrowsAsync<OAuthException>(() => new ResponsesStreamReader().ReadAsync(stream, null, default));
    }
}
static async Task StreamFraming()
{
    using var stream = Stream(":keepalive\r\nevent: response.output_text.delta\r\ndata: {\"type\":\"response.output_text.delta\",\r\ndata: \"delta\":\"hello\"}\r\n\r\n" + Complete("hello"));
    Equal("hello", await new ResponsesStreamReader().ReadAsync(stream, null, default));
    using var blocked = new BlockingStream(); using var cancellation = new CancellationTokenSource(30);
    await ThrowsAsync<OperationCanceledException>(() => new ResponsesStreamReader().ReadAsync(blocked, null, cancellation.Token));
}
static async Task ResponsesContract()
{
    var store = new MemoryStore(); var calls = 0;
    using var http = new HttpClient(new StubHandler(async request =>
    {
        calls++; Equal("Bearer", request.Headers.Authorization!.Scheme); Equal("old-access", request.Headers.Authorization.Parameter!);
        if (request.Method == HttpMethod.Get) return Json("""{"models":[{"slug":"visible-model","display_name":"Visible","visibility":"list"},{"slug":"hidden-model","display_name":"Hidden","visibility":"hide"}]}""");
        Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        True(!body.RootElement.GetProperty("store").GetBoolean()); True(body.RootElement.GetProperty("stream").GetBoolean());
        True(!body.RootElement.TryGetProperty("temperature", out _)); Equal("visible-model", body.RootElement.GetProperty("model").GetString()!);
        return new(HttpStatusCode.OK) { Content = new StringContent(Complete("번역"), Encoding.UTF8, "text/event-stream") };
    }));
    var client = new ChatGptResponsesClient(http, new ChatGptSession(http, store));
    var models = await client.ListModelsAsync(default); Equal(1, models.Count); Equal("visible-model", models[0].Id);
    Equal("번역", await client.TranslateAsync(models[0].Id, "Translate", new[] { new { role = "user", content = "test" } }, null, default)); Equal(2, calls);
}
static async Task UsagePause()
{
    var calls = 0; var store = new MemoryStore();
    using var http = new HttpClient(new StubHandler(_ => { calls++; return Task.FromResult(Json("""{"error":{"code":"subscription_sharing_usage_limit_exceeded"}}""", HttpStatusCode.TooManyRequests)); }));
    var session = new ChatGptSession(http, store); var client = new ChatGptResponsesClient(http, session);
    for (var i = 0; i < 2; i++) await ThrowsAsync<OAuthException>(() => client.TranslateAsync("model", "Translate", "text", null, default));
    Equal(1, calls); session.Resume("client");
    await ThrowsAsync<OAuthException>(() => client.TranslateAsync("model", "Translate", "text", null, default)); Equal(2, calls);
}
static async Task StreamUsagePause()
{
    var calls = 0; var store = new MemoryStore();
    using var http = new HttpClient(new StubHandler(_ =>
    {
        calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Event(new { type = "response.failed", response = new { error = new { code = "subscription_sharing_usage_limit_exceeded" } } })) });
    }));
    var session = new ChatGptSession(http, store); var client = new ChatGptResponsesClient(http, session);
    for (var i = 0; i < 2; i++) await ThrowsAsync<OAuthException>(() => client.TranslateAsync("model", "Translate", "text", null, default));
    Equal(1, calls);
}
static async Task SwitchCancellation()
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var http = new HttpClient(new StubHandler(_ =>
    {
        started.SetResult(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) });
    }));
    var session = new ChatGptSession(http, new MemoryStore()); var client = new ChatGptResponsesClient(http, session);
    var pending = client.TranslateAsync("model", "Translate", "text", null, default);
    await started.Task; await session.SelectAsync("client", default);
    await ThrowsAsync<OperationCanceledException>(() => pending);
}
static async Task BoundedInference()
{
    var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    var second = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    using var http = new HttpClient(new StubHandler(_ =>
    {
        return Interlocked.Increment(ref calls) switch
        {
            1 => first.Task,
            2 => second.Task,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Complete("latest")) })
        };
    }));
    var client = new ChatGptResponsesClient(http, new ChatGptSession(http, new MemoryStore()));
    var a = client.TranslateAsync("model", "Translate", "a", null, default);
    var b = client.TranslateAsync("model", "Translate", "b", null, default);
    var stale = client.TranslateAsync("model", "Translate", "stale", null, default);
    var latest = client.TranslateAsync("model", "Translate", "latest", null, default);
    Equal(2, calls);
    first.SetResult(new(HttpStatusCode.OK) { Content = new StringContent(Complete("a")) });
    Equal("a", await a);
    await ThrowsAsync<OperationCanceledException>(() => stale);
    Equal("latest", await latest);
    second.SetResult(new(HttpStatusCode.OK) { Content = new StringContent(Complete("b")) });
    await b; Equal(3, calls);
}
static async Task SafeDiagnostics()
{
    using var response = Json("""{"detail":"do not echo this private text"}""", HttpStatusCode.Forbidden);
    response.Headers.Add("x-request-id", "req_abc-123");
    var error = await ChatGptSession.ReadErrorAsync(response, default);
    True(error.Message.Contains("req_abc-123")); True(!error.Message.Contains("private")); Equal(403, error.StatusCode!.Value);
}
static async Task OrderedQueue()
{
    Translator.Logs.Clear(); var queue = new TranslationTaskQueue();
    var old = new TaskCompletionSource<(string, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
    var newer = new TaskCompletionSource<(string, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
    Action<string>? oldProgress = null; Action<string>? newProgress = null;
    queue.Enqueue((token, progress) => { oldProgress = progress; return old.Task; }, "old");
    queue.Enqueue((token, progress) => { newProgress = progress; return newer.Task; }, "new");
    newProgress!("new preview"); oldProgress!("stale"); Equal("new preview", queue.Output.translatedText); Equal(0, Translator.Logs.Count);
    newer.SetResult(("new final", true)); await WaitUntil(() => Translator.Logs.Count == 1);
    old.SetResult(("stale final", true)); await Task.Delay(30);
    Equal("new final", queue.Output.translatedText); Equal(1, Translator.Logs.Count);
}
static async Task CanceledQueue()
{
    Translator.Logs.Clear(); var queue = new TranslationTaskQueue();
    var pending = new TaskCompletionSource<(string, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
    queue.Enqueue((token, progress) => { progress("preview"); return pending.Task; }, "source");
    Equal("preview", queue.Output.translatedText); pending.SetCanceled();
    await WaitUntil(() => queue.Output.translatedText == ""); Equal(0, Translator.Logs.Count);
}
static Task ChatPreview() => Check(() =>
{
    var chat = new ChatTranscript();
    True(chat.IsEmpty);
    chat.Apply(new(1, "hello", "", ChatUpdateKind.Started));
    True(!chat.IsEmpty); Equal("hello", chat.LiveMessage!.Source);
    Equal("", chat.LiveMessage.Translation); True(chat.LiveMessage.IsStreaming);
    var started = chat.LiveMessage!.ReceivedAt;
    chat.Apply(new(1, "hello", "안", ChatUpdateKind.Preview));
    chat.Apply(new(1, "hello", "안녕하세요", ChatUpdateKind.Preview));
    Equal(0, chat.Messages.Count); Equal("hello", chat.LiveMessage!.Source);
    Equal("안녕하세요", chat.LiveMessage.Translation); Equal(started, chat.LiveMessage.ReceivedAt);
    chat.Apply(new(1, "hello", "안녕하세요", ChatUpdateKind.Completed));
    Equal(1, chat.Messages.Count); True(chat.LiveMessage == null); True(!chat.IsEmpty);
    Equal(started, chat.Messages[0].ReceivedAt); True(!chat.Messages[0].IsStreaming);
});
static Task ChatOrdering() => Check(() =>
{
    var chat = new ChatTranscript();
    chat.Apply(new(2, "new", "", ChatUpdateKind.Started));
    chat.Apply(new(1, "old", "이전 대화", ChatUpdateKind.Completed));
    Equal(1, chat.Messages.Count); Equal("new", chat.LiveMessage!.Source);
    chat.Apply(new(1, "old", "stale", ChatUpdateKind.Preview));
    chat.Apply(new(1, "old", "", ChatUpdateKind.Canceled));
    Equal("", chat.LiveMessage!.Translation);
    chat.Apply(new(2, "new", "새 대화", ChatUpdateKind.Preview));
    Equal("새 대화", chat.LiveMessage!.Translation);
    chat.Apply(new(2, "new", "새 대화", ChatUpdateKind.Completed));
    chat.Apply(new(2, "new", "late partial", ChatUpdateKind.Preview));
    Equal(2, chat.Messages.Count); True(chat.LiveMessage == null);
});
static Task ChatHistory() => Check(() =>
{
    var chat = new ChatTranscript(2);
    chat.Apply(new(1, "enemy", "적", ChatUpdateKind.Completed));
    chat.Apply(new(2, "enemy ahead", "전방에 적", ChatUpdateKind.Completed, true));
    Equal(1, chat.Messages.Count); Equal("전방에 적", chat.Messages[0].Translation);
    chat.Apply(new(3, "left", "왼쪽", ChatUpdateKind.Completed));
    chat.Apply(new(4, "right", "오른쪽", ChatUpdateKind.Completed));
    Equal(2, chat.Messages.Count); Equal("left", chat.Messages[0].Source);
    Equal("right", chat.Messages[1].Source);
});
static Task ChatPauseAndError() => Check(() =>
{
    var chat = new ChatTranscript();
    chat.Apply(new(1, "done", "완료", ChatUpdateKind.Completed));
    chat.Apply(new(2, "new", "일부", ChatUpdateKind.Preview));
    chat.SetPaused(true);
    True(chat.IsPaused); True(chat.LiveMessage == null);
    chat.Apply(new(2, "new", "ignored", ChatUpdateKind.Completed));
    Equal(1, chat.Messages.Count);
    chat.SetPaused(false);
    chat.Apply(new(3, "failed", "[ERROR] Check sign-in.", ChatUpdateKind.Error));
    True(chat.HasError); Equal("Check sign-in.", chat.Status); Equal(1, chat.Messages.Count);
    chat.Apply(new(4, "retry", "다시", ChatUpdateKind.Preview));
    True(!chat.HasError);
    chat.Apply(new(4, "retry", "", ChatUpdateKind.Canceled));
    True(chat.LiveMessage == null); Equal(1, chat.Messages.Count);
});
static async Task ChatQueueEvents()
{
    var queue = new TranslationTaskQueue();
    var updates = new System.Collections.Concurrent.ConcurrentQueue<ChatUpdate>();
    queue.CaptionUpdated += updates.Enqueue;
    queue.Enqueue((token, progress) => { progress("안녕"); return Task.FromResult(("안녕하세요", false)); }, "hello");
    await WaitUntil(() => updates.Any(x => x.Kind == ChatUpdateKind.Completed));
    True(updates.All(x => x.Source == "hello"));
    Equal(1, updates.Count(x => x.Kind == ChatUpdateKind.Preview));
    Equal(ChatUpdateKind.Started, updates.First().Kind);
    queue.Enqueue(async (token, progress) => { progress("partial"); await Task.Delay(Timeout.Infinite, token); return ("never", false); }, "canceled");
    queue.CancelAll();
    await WaitUntil(() => updates.Any(x => x.Source == "canceled" && x.Kind == ChatUpdateKind.Canceled));
    True(!updates.Any(x => x.Source == "canceled" && x.Kind == ChatUpdateKind.Completed));
}
static async Task ChatSourceFirst()
{
    Translator.Logs.Clear();
    var queue = new TranslationTaskQueue();
    var chat = new ChatTranscript();
    var updates = new System.Collections.Concurrent.ConcurrentQueue<ChatUpdate>();
    var result = new TaskCompletionSource<(string, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
    queue.CaptionUpdated += updates.Enqueue;
    queue.Enqueue(token =>
    {
        True(updates.TryPeek(out var update));
        Equal(ChatUpdateKind.Started, update!.Kind);
        return result.Task;
    }, "hello");
    True(updates.TryDequeue(out var started));
    chat.Apply(started!);
    Equal("hello", chat.LiveMessage!.Source); Equal("", chat.LiveMessage.Translation);
    Equal(0, chat.Messages.Count); Equal(0, Translator.Logs.Count);
    var received = chat.LiveMessage.ReceivedAt;
    result.SetResult(("안녕하세요", false));
    await WaitUntil(() => updates.Any(x => x.Kind == ChatUpdateKind.Completed));
    foreach (var update in updates) chat.Apply(update);
    True(chat.LiveMessage == null); Equal(1, chat.Messages.Count);
    Equal("hello", chat.Messages[0].Source); Equal("안녕하세요", chat.Messages[0].Translation);
    Equal(received, chat.Messages[0].ReceivedAt); Equal(1, Translator.Logs.Count);
}
static string CombinedCaptionJson(string translation = "같이 이동할래?") => JsonSerializer.Serialize(new
{
    translation,
    replies = new[]
    {
        new { text = "On my way.", meaning = "지금 갈게." },
        new { text = "Give me a second.", meaning = "잠깐만." },
        new { text = "Go ahead.", meaning = "먼저 가." }
    }
});

static Task CombinedJsonPreview() => Check(() =>
{
    const string translation = "그가 \"이동\"이라고 했어. 🚁";
    var json = CombinedCaptionJson(translation);
    var boundary = json.IndexOf(",\"replies\"", StringComparison.Ordinal);
    for (var length = 0; length <= json.Length; length++)
    {
        var preview = WardogsCaptionResponse.ReadTranslationPreview(json[..length]);
        if (length < boundary) True(preview == null);
        else Equal(translation, preview!);
    }
    var parsed = WardogsCaptionResponse.Parse(json);
    Equal(translation, parsed.Text); Equal(3, parsed.Replies.Count); True(parsed.RepliesRequested);
    Equal("On my way.", parsed.Replies[0].Text); Equal("지금 갈게.", parsed.Replies[0].Meaning);
    True(WardogsCaptionResponse.ReadTranslationPreview("{\"replies\":[{\"translation\":\"nested\"}]") == null);
});

static Task CombinedJsonValidation() => Check(() =>
{
    foreach (var replies in new[]
    {
        "null", "[]", "[null,null,null]", "{}",
        """[{"text":"yes","meaning":"네"},{"text":"YES","meaning":"네"},{"text":"no","meaning":"아니"}]""",
        """[{"text":"yes","meaning":"네"},{"text":"wait","meaning":null},{"text":"no","meaning":"아니"}]""",
        """[{"text":"yes\nnow","meaning":"네"},{"text":"wait","meaning":"잠깐"},{"text":"no","meaning":"아니"}]"""
    })
    {
        var result = WardogsCaptionResponse.Parse("{\"translation\":\"번역\",\"replies\":" + replies + "}");
        Equal("번역", result.Text); Equal(0, result.Replies.Count); True(result.RepliesRequested);
    }
    Equal("번역", WardogsCaptionResponse.Parse("{\"translation\":\"번역\",\"replies\":[").Text);
    Equal(0, WardogsCaptionResponse.Parse(CombinedCaptionJson().Replace("On my way.", new string('x', 161))).Replies.Count);
    Throws<FormatException>(() => WardogsCaptionResponse.Parse("not JSON"));
    Throws<FormatException>(() => WardogsCaptionResponse.Parse("{\"translation\":null}"));
    Throws<FormatException>(() => WardogsCaptionResponse.Parse("{\"translation\":\"\"}"));
});

static async Task CombinedRequest()
{
    var calls = 0;
    var json = CombinedCaptionJson();
    var boundary = json.IndexOf(",\"replies\"", StringComparison.Ordinal);
    var firstCharacter = json.IndexOf("\\u", StringComparison.Ordinal) + 6;
    using var http = new HttpClient(new StubHandler(async request =>
    {
        calls++;
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var instructions = body.RootElement.GetProperty("instructions").GetString()!;
        True(instructions.Contains("translation\" property FIRST"));
        True(instructions.Contains("CURRENT SOURCE"));
        var format = body.RootElement.GetProperty("text").GetProperty("format");
        Equal("json_schema", format.GetProperty("type").GetString()!);
        True(format.GetProperty("strict").GetBoolean());
        var schema = format.GetProperty("schema");
        Equal("translation", schema.GetProperty("properties").EnumerateObject().First().Name);
        True(!schema.GetProperty("additionalProperties").GetBoolean());
        var repliesSchema = schema.GetProperty("properties").GetProperty("replies");
        Equal(3, repliesSchema.GetProperty("minItems").GetInt32());
        Equal(3, repliesSchema.GetProperty("maxItems").GetInt32());
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                Event(new { type = "response.output_text.delta", delta = json[..firstCharacter] }) +
                Event(new { type = "response.output_text.delta", delta = json[firstCharacter..boundary] }) +
                Event(new { type = "response.output_text.delta", delta = json[boundary..] }) + Complete(json))
        };
    }));
    var client = new ChatGptResponsesClient(http, new ChatGptSession(http, new MemoryStore()));
    var previews = new List<string>();
    var result = await client.TranslateCaptionAsync("model", "Translate into Korean", "Want to move together?", true, previews.Add, default);
    Equal(1, calls); Equal(2, previews.Count); Equal("같", previews[0]); Equal("같이 이동할래?", previews[1]);
    Equal("같이 이동할래?", result.Text); Equal(3, result.Replies.Count);
}

static async Task OrdinaryCaptionRequest()
{
    var calls = 0;
    using var http = new HttpClient(new StubHandler(async request =>
    {
        calls++;
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Equal("Translate only", body.RootElement.GetProperty("instructions").GetString()!);
        True(!body.RootElement.TryGetProperty("text", out _));
        return new(HttpStatusCode.OK) { Content = new StringContent(Event(new { type = "response.output_text.delta", delta = "번" }) + Complete("번역")) };
    }));
    var client = new ChatGptResponsesClient(http, new ChatGptSession(http, new MemoryStore()));
    var previews = new List<string>();
    var result = await client.TranslateCaptionAsync("model", "Translate only", "source", false, previews.Add, default);
    Equal(1, calls); Equal("번", previews.Single()); Equal("번역", result.Text);
    Equal(0, result.Replies.Count); True(!result.RepliesRequested);
}

static async Task ReplyQueueOrdering()
{
    Translator.Logs.Clear();
    var queue = new TranslationTaskQueue();
    var updates = new System.Collections.Concurrent.ConcurrentQueue<ChatUpdate>();
    queue.CaptionUpdated += updates.Enqueue;
    var old = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    queue.EnqueueResult((token, progress) => old.Task, "old source");
    var result = WardogsCaptionResponse.Parse(CombinedCaptionJson());
    queue.EnqueueResult((token, progress) => { progress(result.Text); return Task.FromResult(result); }, "new source");
    await WaitUntil(() => updates.Any(x => x.Kind == ChatUpdateKind.Completed));
    old.SetResult(WardogsCaptionResponse.Parse(CombinedCaptionJson("stale translation")));
    await WaitUntil(() => updates.Any(x => x.Source == "old source" && x.Kind == ChatUpdateKind.Canceled));
    Equal(1, Translator.Logs.Count); Equal(result.Text, Translator.Logs.Single()); Equal(result.Text, queue.Output.translatedText);
    var chat = new ChatTranscript();
    foreach (var update in updates) chat.Apply(update);
    Equal(1, chat.Messages.Count); Equal("new source", chat.Messages[0].Source); Equal(3, chat.Messages[0].Replies.Count);
    True(chat.Messages[0].HasReplies); True(!chat.Messages[0].RepliesUnavailable);
    chat.Apply(new(3, "revised", "수정", ChatUpdateKind.Completed, true));
    Equal(1, chat.Messages.Count); True(!chat.Messages[0].HasReplies);
    chat.Apply(new(4, "invalid replies", "번역", ChatUpdateKind.Completed) { RepliesRequested = true });
    True(chat.Messages[1].RepliesUnavailable); True(!chat.Messages[1].HasReplies);
}

static async Task ReasoningRequestSettings()
{
    foreach (var (model, effort) in new[]
    {
        ("gpt-6.1-terra", "none"), ("gpt-6.1-terra", "low"), ("gpt-6.1-terra", "medium"),
        ("gpt-6.1-terra", "high"), ("gpt-6.1-terra", "xhigh"), ("gpt-6.1-terra", "max"),
        ("gpt-6.1-terra", "default"), ("gpt-6-astra", "low"), ("gpt-6.1-sol", "high"),
        ("gpt-6-luna", "none"), ("other-model", "minimal"), ("other-model", "default")
    })
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Equal(model, body.RootElement.GetProperty("model").GetString()!);
            Equal(effort != "default", body.RootElement.TryGetProperty("reasoning", out var reasoning));
            if (effort != "default") Equal(effort, reasoning.GetProperty("effort").GetString()!);
            True(!body.RootElement.TryGetProperty("service_tier", out _));
            return new(HttpStatusCode.OK) { Content = new StringContent(Complete(CombinedCaptionJson())) };
        }));
        var client = new ChatGptResponsesClient(http, new ChatGptSession(http, new MemoryStore()));
        var result = await client.TranslateCaptionAsync(model, "Translate", "source", true, null, default, effort);
        Equal(1, calls); Equal(3, result.Replies.Count);
    }
    using var neverCalled = new HttpClient(new StubHandler(_ => throw new Exception("Unsupported choice must fail before HTTP")));
    var guarded = new ChatGptResponsesClient(neverCalled, new ChatGptSession(neverCalled, new MemoryStore()));
    await ThrowsAsync<OAuthException>(() => guarded.TranslateAsync("gpt-6-astra", "Translate", "source", null, default, "none"));
    await ThrowsAsync<OAuthException>(() => guarded.TranslateAsync("model", "Translate", "source", null, default, "invalid"));
}

static Task ModelPreferences() => Check(() =>
{
    var config = new ChatGptOAuthConfig { ModelName = "gpt-6.1-terra" };
    config.SelectCatalogAccount("account-a");
    config.StoreCatalog("account-a", [new("gpt-6.1-terra", "Terra"), new("gpt-6.1-sol", "Sol")]);
    config.ReasoningEffort = "none";
    config.ModelName = "gpt-6.1-sol"; Equal("default", config.ReasoningEffort);
    config.ReasoningEffort = "high";
    config.ModelName = "gpt-6.1-terra"; Equal("none", config.ReasoningEffort);
    config.SelectCatalogAccount("account-b");
    True(!config.HasCachedCatalog); Equal(0, config.CachedModels.Count); Equal("", config.ModelName);
    config.StoreCatalog("account-b", [new("gpt-6-luna", "Luna")]);
    config.ModelName = "gpt-6-luna"; config.ReasoningEffort = "low";
    var restored = JsonSerializer.Deserialize<ChatGptOAuthConfig>(JsonSerializer.Serialize(config))!;
    Equal("gpt-6-luna", restored.ModelName); Equal("low", restored.ReasoningEffort); True(restored.HasCachedCatalog);
    restored.SelectCatalogAccount("account-a");
    Equal(2, restored.CachedModels.Count); Equal("gpt-6.1-terra", restored.ModelName); Equal("none", restored.ReasoningEffort);
    restored.ModelName = "gpt-6.1-sol"; Equal("high", restored.ReasoningEffort);
    True(!restored.ReasoningOptions.Any(x => x.Value == "none"));
    restored.ReasoningEffort = "none"; Equal("high", restored.ReasoningEffort);
    restored.SelectCatalogAccount(""); Equal("", restored.ModelName); Equal(0, restored.CachedModels.Count);
    restored.SelectCatalogAccount("account-b"); Equal("gpt-6-luna", restored.ModelName); Equal("low", restored.ReasoningEffort);
    restored.StoreCatalog("account-b", []); True(restored.HasCachedCatalog); Equal("", restored.ModelName);
    Equal("low", restored.GetReasoningEffort("gpt-6-luna"));
});

static Task ReasoningMigration() => Check(() =>
{
    var enabled = JsonSerializer.Deserialize<ChatGptOAuthConfig>("""{"FastTranslation":true,"ModelName":"gpt-6.1-terra"}""")!;
    Equal("none", enabled.ReasoningEffort);
    True(!JsonSerializer.Serialize(enabled).Contains("FastTranslation"));
    var disabled = JsonSerializer.Deserialize<ChatGptOAuthConfig>("""{"ModelName":"gpt-6.1-terra","FastTranslation":false}""")!;
    Equal("default", disabled.ReasoningEffort);
    var explicitChoice = JsonSerializer.Deserialize<ChatGptOAuthConfig>("""{"FastTranslation":true,"ModelReasoningEfforts":{"gpt-6.1-terra":"medium"},"ModelName":"gpt-6.1-terra"}""")!;
    Equal("medium", explicitChoice.ReasoningEffort);
    Equal("default", new ChatGptOAuthConfig { ModelName = "gpt-6.1-terra" }.ReasoningEffort);
});

static async Task CachedModelCatalog()
{
    var calls = 0;
    using var http = new HttpClient(new StubHandler(_ =>
    {
        calls++;
        if (calls == 3) return Task.FromResult(Json("{}", HttpStatusCode.InternalServerError));
        return Task.FromResult(Json("""{"models":[{"slug":"gpt-6.1-terra","display_name":"Terra","visibility":"list"}]}"""));
    }));
    var client = new ChatGptResponsesClient(http, new ChatGptSession(http, new MemoryStore()));
    var config = new ChatGptOAuthConfig(); config.SelectCatalogAccount("client");
    Equal(1, (await client.GetModelsAsync(config, "client", false, default)).Count);
    config.ModelName = "gpt-6.1-terra"; config.ReasoningEffort = "low";
    var restored = JsonSerializer.Deserialize<ChatGptOAuthConfig>(JsonSerializer.Serialize(config))!;
    await client.GetModelsAsync(restored, "client", false, default); Equal(1, calls);
    Equal("gpt-6.1-terra", restored.ModelName); Equal("low", restored.ReasoningEffort);
    await client.GetModelsAsync(restored, "client", true, default); Equal(2, calls);
    await ThrowsAsync<OAuthException>(() => client.GetModelsAsync(restored, "client", true, default)); Equal(3, calls);
    Equal(1, restored.CachedModels.Count); Equal("gpt-6.1-terra", restored.ModelName);
    await client.GetModelsAsync(restored, "client", false, default); Equal(3, calls);
    restored.SelectCatalogAccount("another-client");
    True(!restored.HasCachedCatalog); Equal(0, restored.CachedModels.Count);
    await ThrowsAsync<OperationCanceledException>(() => client.GetModelsAsync(restored, "client", false, default));
    Equal(3, calls);
    restored.StoreCatalog("another-client", []);
    Equal(0, (await client.GetModelsAsync(restored, "another-client", false, default)).Count); Equal(3, calls);
}

static Task PartialJsonPreview() => Check(() =>
{
    const string translation = "그가 \"이동\"이라고 했어. 🚁 / \\ 끝";
    var json = CombinedCaptionJson(translation);
    var boundary = json.IndexOf(",\"replies\"", StringComparison.Ordinal);
    var sawEarlyText = false;
    for (var length = 0; length <= json.Length; length++)
    {
        var preview = WardogsCaptionResponse.ReadTranslationPreview(json[..length], allowPartial: true);
        if (string.IsNullOrEmpty(preview)) continue;
        True(translation.StartsWith(preview, StringComparison.Ordinal));
        True(!char.IsHighSurrogate(preview[^1]));
        if (length < boundary) sawEarlyText = true;
        else Equal(translation, preview);
    }
    True(sawEarlyText);
    Equal("이동", WardogsCaptionResponse.ReadTranslationPreview("{\"translation\":\"이동", true)!);
    Equal("A", WardogsCaptionResponse.ReadTranslationPreview("{\"translation\":\"A\\uD83D", true)!);
    Equal("A", WardogsCaptionResponse.ReadTranslationPreview("{\"translation\":\"A\uD83D", true)!);
    True(WardogsCaptionResponse.ReadTranslationPreview("{\"replies\":[{\"translation\":\"bad", true) == null);
    True(WardogsCaptionResponse.ReadTranslationPreview("{\"translation\":\"bad\\q", true) == null);
    Throws<FormatException>(() => WardogsCaptionResponse.Parse("{\"translation\":\"unfinished"));
});

static Task TranslationCacheExpiry() => Check(() =>
{
    var clock = new ManualTimeProvider();
    var cache = new TranslationCache(clock: clock);
    Equal(10 * 1024 * 1024L, TranslationCache.DefaultMaxBytes);
    Equal(TimeSpan.FromMinutes(15), TranslationCache.DefaultTtl);
    cache.Store("first", new("첫 문장"));
    clock.Advance(TimeSpan.FromMinutes(1)); cache.Store("second", new("둘째 문장"));
    clock.Advance(TimeSpan.FromMinutes(13)); True(cache.TryGet("first", out _));
    clock.Advance(TimeSpan.FromMinutes(1));
    True(!cache.TryGet("first", out _)); True(cache.TryGet("second", out _)); Equal(1, cache.Count);
    clock.Advance(TimeSpan.FromMinutes(1)); Equal(0, cache.Count); Equal(0L, cache.UsedBytes);
    cache.Store("renew", new("old")); clock.Advance(TimeSpan.FromMinutes(14));
    cache.Store("renew", new("new")); clock.Advance(TimeSpan.FromMinutes(1));
    True(cache.TryGet("renew", out var renewed)); Equal("new", renewed!.Text);
});

static Task TranslationCacheBudget() => Check(() =>
{
    var result = new TranslationResult("번역");
    var probe = new TranslationCache(); probe.Store("a", result);
    var budget = probe.UsedBytes * 2;
    var cache = new TranslationCache(budget);
    cache.Store("a", result); cache.Store("b", result); Equal(2, cache.Count);
    True(cache.TryGet("a", out _)); cache.Store("c", result);
    True(!cache.TryGet("b", out _)); True(cache.TryGet("a", out _)); True(cache.TryGet("c", out _));
    True(cache.UsedBytes <= budget);
    cache.Store("large", new(new string('x', (int)budget * 2)));
    Equal(2, cache.Count); True(!cache.TryGet("large", out _));
    cache.Store("a", result); Equal(2, cache.Count); True(cache.UsedBytes <= budget);
    Throws<ArgumentOutOfRangeException>(() => new TranslationCache(0));
    Throws<ArgumentOutOfRangeException>(() => new TranslationCache(ttl: TimeSpan.Zero));
});

static Task TranslationCacheValidation() => Check(() =>
{
    var cache = new TranslationCache();
    cache.Store("error", new("[ERROR] failed")); cache.Store("warning", new("[WARNING] retry"));
    cache.Store("blank", new(" ")); cache.Store("incomplete", new("번역") { RepliesRequested = true });
    Equal(0, cache.Count);
    var choices = WardogsCaptionResponse.Parse(CombinedCaptionJson()).Replies.ToList();
    cache.Store("good", new("번역") { RepliesRequested = true, Replies = choices });
    choices.Clear(); True(cache.TryGet("good", out var copy)); Equal(3, copy!.Replies.Count);
    if (copy.Replies is IList<ReplySuggestion> mutable) mutable[0] = new("changed", "변경됨");
    True(cache.TryGet("good", out var second)); Equal("On my way.", second!.Replies[0].Text);
    var key = TranslationCache.CreateKey(new { source = "secret caption", credential = "private key" });
    True(!key.Contains("secret")); True(!key.Contains("private")); Equal(64, key.Length);
});

static async Task CaptionCacheRequests()
{
    var clock = new ManualTimeProvider(); var cache = new TranslationCache(clock: clock); var calls = 0;
    using var http = new HttpClient(new StubHandler(async request =>
    {
        calls++;
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var combined = body.RootElement.GetProperty("instructions").GetString()!.Contains("replace the translation-only output");
        return new(HttpStatusCode.OK) { Content = new StringContent(Complete(combined ? CombinedCaptionJson() : "번역")) };
    }));
    var session = new ChatGptSession(http, new MemoryStore()); var client = new ChatGptResponsesClient(http, session, cache);
    var first = await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", "source", true, null, default);
    var previews = new List<string>();
    var hit = await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", "source", true, previews.Add, default);
    Equal(1, calls); Equal(first.Text, hit.Text); Equal(3, hit.Replies.Count); Equal(hit.Text, previews.Single());
    clock.Advance(TimeSpan.FromMinutes(14));
    await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", "source", true, null, default); Equal(1, calls);
    clock.Advance(TimeSpan.FromMinutes(1));
    await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", "source", true, null, default); Equal(2, calls);
    await client.TranslateCaptionAsync("gpt-6.1-sol", "ko", "source", true, null, default); Equal(3, calls);
    await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", "source", true, null, default, "low"); Equal(4, calls);
    await client.TranslateCaptionAsync("gpt-6.1-terra", "ja", "source", true, null, default); Equal(5, calls);
    await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", "different source", true, null, default); Equal(6, calls);
    await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", "source", false, null, default); Equal(7, calls);
    await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", new[] { "prior context", "source" }, true, null, default); Equal(8, calls);
    await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", new[] { "prior context", "source" }, true, null, default); Equal(8, calls);
    await session.SelectAsync("client", default);
    await client.TranslateCaptionAsync("gpt-6.1-terra", "ko", "source", true, null, default); Equal(9, calls);
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => client.TranslateCaptionAsync("gpt-6.1-terra", "ko", "source", true, null, canceled.Token));
    Equal(9, calls);
}

static async Task CaptionCacheFailure()
{
    var calls = 0; var cache = new TranslationCache();
    using var http = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(Complete(++calls == 1 ? "not JSON" : CombinedCaptionJson()))
    })));
    var client = new ChatGptResponsesClient(http, new ChatGptSession(http, new MemoryStore()), cache);
    await ThrowsAsync<FormatException>(() => client.TranslateCaptionAsync("model", "translate", "source", true, null, default));
    Equal(0, cache.Count);
    await client.TranslateCaptionAsync("model", "translate", "source", true, null, default); Equal(2, calls); Equal(1, cache.Count);
    using var canceled = new CancellationTokenSource();
    using var streamingHttp = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(Event(new { type = "response.output_text.delta", delta = "partial" }) + Complete("final"))
    })));
    var streamingCache = new TranslationCache();
    var streamingClient = new ChatGptResponsesClient(streamingHttp, new ChatGptSession(streamingHttp, new MemoryStore()), streamingCache);
    await ThrowsAsync<OperationCanceledException>(() => streamingClient.TranslateCaptionAsync("model", "translate", "source", false,
        _ => canceled.Cancel(), canceled.Token));
    Equal(0, streamingCache.Count);
}

static Task TranslationCacheConcurrency() => Check(() =>
{
    const long budget = 16 * 1024;
    var cache = new TranslationCache(budget);
    Parallel.For(0, 1000, i =>
    {
        var key = (i % 50).ToString();
        cache.Store(key, new("번역 " + key));
        if (cache.TryGet(key, out var result)) Equal("번역 " + key, result!.Text);
        True(cache.UsedBytes <= budget);
    });
    True(cache.UsedBytes <= budget);
});

static Task WrappedCaptionJson() => Check(() =>
{
    var json = CombinedCaptionJson();
    foreach (var wrapped in new[] { "```json\n" + json + "\n```", "  \uFEFF" + json,
        "```JSON\r\n" + json + "\r\n```", "```\n" + json + "\n```", json.Replace("translation", "Translation") })
    {
        var result = WardogsCaptionResponse.Parse(wrapped);
        Equal("같이 이동할래?", result.Text); Equal(3, result.Replies.Count);
        Equal(result.Text, WardogsCaptionResponse.ReadTranslationPreview(wrapped)!);
    }
    var partial = "```json\n{\"translation\":\"같이";
    Equal("같이", WardogsCaptionResponse.ReadTranslationPreview(partial, true)!);
    True(WardogsCaptionResponse.ReadTranslationPreview("```js", true) == null);
    Throws<FormatException>(() => WardogsCaptionResponse.Parse("```json\n{\"translation\":\"unfinished"));
    Throws<FormatException>(() => WardogsCaptionResponse.Parse("{\"replies\":[{\"translation\":\"wrong nested field\"}]}"));
});

static async Task WaitUntil(Func<bool> done)
{
    using var timeout = new CancellationTokenSource(2000);
    while (!done()) await Task.Delay(10, timeout.Token);
}

/// <summary>Monotonic test clock advancing expiration without sleeping.</summary>
sealed class ManualTimeProvider : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    internal void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
}

/// <summary>Offline HTTP transport executing an explicit test scenario.</summary>
sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
}

/// <summary>In-memory vault with serialization copies and an exclusive lease matching production store semantics.</summary>
sealed class MemoryStore : IOAuthStore
{
    private string json;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal MemoryStore(bool expired = false)
    {
        json = JsonSerializer.Serialize(new OAuthVault
        {
            ActiveClientId = "client",
            Accounts = [new OAuthAccount
        {
            ClientId = "client", Subject = "subject", AccessToken = "old-access", RefreshToken = "old-refresh",
            IdToken = "retained-id", Scopes = ["openid", "resource.invoke", "chatgpt.tokens.use.direct"],
            ExpiresAt = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1)
        }]
        });
    }
    public async Task<IDisposable> LockAsync(CancellationToken token) { await gate.WaitAsync(token); return new Lease(gate); }
    public OAuthVault Load() => JsonSerializer.Deserialize<OAuthVault>(json)!;
    public void Save(OAuthVault vault) => json = JsonSerializer.Serialize(vault);
    private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
}

/// <summary>A stream that blocks until cancellation, modeling an open SSE connection.</summary>
sealed class BlockingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
}

namespace LiveCaptionsTranslator
{
    /// <summary>History sink used to verify queue ordering without starting Windows Live Captions.</summary>
    internal static class Translator
    {
        internal static SettingsSink Setting { get; } = new();
        internal static readonly System.Collections.Concurrent.ConcurrentQueue<string> Logs = new();
        internal static Task<bool> IsOverwrite(string text) => Task.FromResult(false);
        internal static Task AddContexts() => Task.CompletedTask;
        internal static Task Log(string source, string translation, bool overwrite) { Logs.Enqueue(translation); return Task.CompletedTask; }
    }

    /// <summary>Replaces disk writes from production configuration notifications in offline tests.</summary>
    internal sealed class SettingsSink
    {
        internal void Save() { }
    }
}
