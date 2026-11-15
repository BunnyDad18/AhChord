using System.Net;
using System.Text;
using WebApi.Services;

namespace WebApi.Tests;

public class LlmClientTests
{
    /// <summary>Scriptable handler: records requests, returns canned responses or throws.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) { Requests.Add(request); }
            return await _responder(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LlmClient NewClient(FakeHandler handler, ILlmEndpointPolicy? endpoints = null) =>
        new(new HttpClient(handler), endpoints: endpoints);

    /// <summary>ST-30: settings are resolved per call — this helper builds them for the tests.</summary>
    private static LlmEffectiveSettings Settings(
        string? baseUrl = "http://llm.test/v1",
        string? model = "test-model",
        string? apiKey = null,
        int maxTokens = 4096,
        string? source = "user") => new(baseUrl, model, apiKey, maxTokens, source ?? (baseUrl is null ? "none" : "user"));

    // --- configuration ---------------------------------------------------------

    [Fact]
    public async Task ConvertAsync_WithoutBaseUrl_ThrowsNotConfigured()
    {
        var client = NewClient(new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}"))));
        await Assert.ThrowsAsync<LlmNotConfiguredException>(() => client.ConvertAsync(Settings(baseUrl: null), "sys", "user"));
    }

    // --- request mapping -------------------------------------------------------

    [Fact]
    public async Task ConvertAsync_PostsChatCompletions_WithSystemUserMessages_AndTemperature()
    {
        var handler = new FakeHandler(req => Task.FromResult(Json(HttpStatusCode.OK, """
            { "choices": [ { "message": { "role": "assistant", "content": "  VERSE\n[G]hello world  " } } ] }
            """)));
        var client = NewClient(handler);

        var result = await client.ConvertAsync(Settings(), "SYSTEM PROMPT", "USER CONTENT");

        Assert.Equal("VERSE\n[G]hello world", result); // trimmed
        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.EndsWith("/v1/chat/completions", req.RequestUri!.ToString());
        var body = await req.Content!.ReadAsStringAsync();
        Assert.Contains("\"model\":\"test-model\"", body);
        Assert.Contains("\"temperature\":0.2", body);
        Assert.Contains("\"max_tokens\":4096", body);
        Assert.Contains("SYSTEM PROMPT", body);
        Assert.Contains("USER CONTENT", body);
        Assert.Contains("\"system\"", body);
        Assert.Contains("\"user\"", body);
    }

    [Fact]
    public async Task ConvertAsync_UsesMaxTokens_FromSettings()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "ok" } } ] }""")));
        var client = NewClient(handler);

        await client.ConvertAsync(Settings(maxTokens: 1234), "s", "u");

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("\"max_tokens\":1234", body);
    }

    [Fact]
    public async Task ConvertAsync_SendsBearerKey_WhenApiKeySet()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "ok" } } ] }""")));
        var client = NewClient(handler);

        await client.ConvertAsync(Settings(apiKey: "sekret"), "s", "u");

        Assert.Equal("Bearer sekret", handler.Requests[0].Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ConvertAsync_SkipsBearer_WhenApiKeyIsNotNeeded()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "ok" } } ] }""")));
        var client = NewClient(handler);

        await client.ConvertAsync(Settings(apiKey: "not-needed"), "s", "u");

        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task ConvertAuth_HeaderIsPerRequest_NotSharedAcrossCalls()
    {
        // ST-30 regression guard: the shared HttpClient must not leak one user's key into another call.
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "ok" } } ] }""")));
        var client = NewClient(handler);

        await client.ConvertAsync(Settings(apiKey: "user-a-key"), "s", "u");
        await client.ConvertAsync(Settings(), "s", "u"); // second user has no key

        Assert.Equal("Bearer user-a-key", handler.Requests[0].Headers.Authorization?.ToString());
        Assert.Null(handler.Requests[1].Headers.Authorization);
    }

    [Fact]
    public async Task ConvertAsync_TrimsTrailingSlash_FromBaseUrl()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "ok" } } ] }""")));
        var client = NewClient(handler);

        await client.ConvertAsync(Settings(baseUrl: "http://llm.test/v1/"), "s", "u");

        Assert.Equal("http://llm.test/v1/chat/completions", handler.Requests[0].RequestUri!.ToString());
    }

    // --- error mapping ---------------------------------------------------------

    [Fact]
    public async Task ConvertAsync_ServerError_NamesTheStatus_ButNotTheUpstreamBody()
    {
        // A member-configured endpoint is attacker-chosen; its body must not travel into the
        // import draft (ChordProConversionJob Fail(lastError)) — the status code is enough for the user.
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.InternalServerError, """
            { "error": { "message": "model exploded" } }
            """)));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertAsync(Settings(), "s", "u"));
        Assert.Contains("500", ex.Message);
        Assert.DoesNotContain("model exploded", ex.Message);
    }

    [Fact]
    public async Task ConvertAsync_ServerError_OnAnEnvEndpoint_EchoesTheProviderHint()
    {
        // LLM_BASE_URL is the operator's own endpoint: the hint is their diagnostic, not a
        // member's echo channel.
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.InternalServerError, """
            { "error": { "message": "model exploded" } }
            """)));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.ConvertAsync(Settings(source: "env"), "s", "u"));
        Assert.Contains("500", ex.Message);
        Assert.Contains("model exploded", ex.Message);
    }

    [Fact]
    public async Task ConvertAsync_EmptyContent_ThrowsLlmException()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            { "choices": [ { "message": { "content": "" } } ] }
            """)));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertAsync(Settings(), "s", "u"));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertAsync_InvalidJson_ThrowsLlmException()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "this is not json")));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertAsync(Settings(), "s", "u"));
        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public async Task ConvertAsync_NetworkFailure_ThrowsLlmException()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused 127.0.0.1:1234"));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertAsync(Settings(), "s", "u"));
        Assert.Contains("Could not reach the configured LLM endpoint", ex.Message);
        // This text reaches the import draft: no host, no port, no provider exception text.
        Assert.DoesNotContain("llm.test", ex.Message);
        Assert.DoesNotContain("127.0.0.1", ex.Message);
        Assert.DoesNotContain("connection refused", ex.Message);
    }

    // --- SSRF: the endpoint policy at fetch time  ---

    [Fact]
    public async Task ConvertAsync_MemberEndpoint_OnAPrivateHost_IsRefusedUnderAClosedPolicy()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var client = NewClient(handler, endpoints: new LlmEndpointPolicy(allowPrivate: false));

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.ConvertAsync(Settings(baseUrl: "http://169.254.169.254/latest/meta-data"), "s", "u"));

        Assert.Contains("policy", ex.Message);
        Assert.DoesNotContain("169.254.169.254", ex.Message);   // the draft must not learn the target
        Assert.Empty(handler.Requests);                          // refused before any socket
    }

    [Fact]
    public async Task ConvertAsync_Streaming_MemberEndpoint_OnAPrivateHost_IsRefusedUnderAClosedPolicy()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var client = NewClient(handler, endpoints: new LlmEndpointPolicy(allowPrivate: false));

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.ConvertStreamingAsync(Settings(baseUrl: "http://127.0.0.1:1234/v1"), "s", "u"));

        Assert.Contains("LLM_ALLOW_PRIVATE_NETWORK", ex.Message);
        Assert.DoesNotContain("127.0.0.1", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ConvertAsync_OwnEnvEndpoint_IsNotPoliced()
    {
        // LLM_BASE_URL is operator config, not member input — a closed policy does not blind
        // the server's own local model.
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            { "choices": [ { "message": { "content": "[G]ok" } } ] }
            """)));
        var client = NewClient(handler, endpoints: new LlmEndpointPolicy(allowPrivate: false));

        var result = await client.ConvertAsync(
            Settings(baseUrl: "http://host.docker.internal:1234/v1", source: "env"), "s", "u");

        Assert.Equal("[G]ok", result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConvertAsync_MemberEndpoint_OnAPublicHost_IsNotPoliced()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            { "choices": [ { "message": { "content": "[G]ok" } } ] }
            """)));
        var client = NewClient(handler, endpoints: new LlmEndpointPolicy(allowPrivate: false));

        var result = await client.ConvertAsync(
            Settings(baseUrl: "https://api.openai.com/v1"), "s", "u");

        Assert.Equal("[G]ok", result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConvertAsync_DefaultPolicy_KeepsTheLocalModel()
    {
        // No policy injected (the ctor default, and every existing caller) → loopback works.
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            { "choices": [ { "message": { "content": "[G]ok" } } ] }
            """)));
        var client = new LlmClient(new HttpClient(handler));

        var result = await client.ConvertAsync(
            Settings(baseUrl: "http://127.0.0.1:1234/v1"), "s", "u");

        Assert.Equal("[G]ok", result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConvertAsync_Timeout_ThrowsLlmException()
    {
        var handler = new FakeHandler(_ =>
        {
            Thread.Sleep(300);
            throw new TaskCanceledException("timed out");
        });
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertAsync(Settings(), "s", "u"));
        Assert.Contains("timed out", ex.Message);
    }

    // --- streaming (ST-33) ------------------------------------------------------

    private static HttpResponseMessage Sse(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    [Fact]
    public async Task ConvertStreamingAsync_AccumulatesChunks_AndInvokesOnChunk()
    {
        var sse = """
            data: {"choices":[{"delta":{"content":"VERSE\n"}}]}

            : keep-alive

            data: {"choices":[{"delta":{"content":"[G]hello "}}]}
            data: {"choices":[{"delta":{"content":"world  "}}]}

            data: [DONE]
            """;
        var handler = new FakeHandler(_ => Task.FromResult(Sse(HttpStatusCode.OK, sse)));
        var client = NewClient(handler);
        var chunks = new List<string>();

        var result = await client.ConvertStreamingAsync(Settings(), "s", "u", onChunk: async c => chunks.Add(c));

        Assert.Equal("VERSE\n[G]hello world", result); // accumulated + trimmed
        Assert.Equal(new[] { "VERSE\n", "[G]hello ", "world  " }, chunks);
        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("\"stream\":true", body);
    }

    [Fact]
    public async Task ConvertStreamingAsync_ServerError_OnAnEnvEndpoint_EchoesTheProviderHint()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Sse(HttpStatusCode.InternalServerError, """
            { "error": { "message": "model exploded" } }
            """)));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.ConvertStreamingAsync(Settings(source: "env"), "s", "u"));
        Assert.Contains("500", ex.Message);
        Assert.Contains("model exploded", ex.Message);
    }

    [Fact]
    public async Task ConvertStreamingAsync_ServerError_OnAMemberEndpoint_DropsTheUpstreamBody()
    {
        // The streaming path is the one the conversion job uses: its text lands in the import draft.
        var handler = new FakeHandler(_ => Task.FromResult(Sse(HttpStatusCode.InternalServerError, """
            { "error": { "message": "model exploded" } }
            """)));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertStreamingAsync(Settings(), "s", "u"));
        Assert.Contains("500", ex.Message);
        Assert.DoesNotContain("model exploded", ex.Message);
    }

    [Fact]
    public async Task ConvertStreamingAsync_EmptyStream_ThrowsLlmException()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Sse(HttpStatusCode.OK, "data: [DONE]\n")));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertStreamingAsync(Settings(), "s", "u"));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertStreamingAsync_InvalidChunkJson_ThrowsLlmException()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Sse(HttpStatusCode.OK, "data: not-json\n")));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertStreamingAsync(Settings(), "s", "u"));
        Assert.Contains("invalid JSON", ex.Message);
    }

    [Fact]
    public async Task ConvertStreamingAsync_WithoutBaseUrl_ThrowsNotConfigured()
    {
        var client = NewClient(new FakeHandler(_ => Task.FromResult(Sse(HttpStatusCode.OK, ""))));
        await Assert.ThrowsAsync<LlmNotConfiguredException>(() => client.ConvertStreamingAsync(Settings(baseUrl: null), "s", "u"));
    }

    // --- ST-58: JSON-shape guards + mid-stream timeout mapping -------------------

    [Fact]
    public async Task ConvertStreamingAsync_EmptyChoicesAndNonStringDeltas_AreSkipped()
    {
        // Real OpenAI-compatible providers emit keep-alive/done chunks with `choices: []`
        // and occasional non-string delta nodes — these used to throw
        // ArgumentOutOfRangeException / InvalidOperationException past the JsonException catch.
        var sse = string.Join('\n',
            """data: {"choices":[]}""",
            """data: {}""",
            """data: {"choices":[{"delta":{"content":42}}]}""",
            """data: {"choices":["not-an-object"]}""",
            """data: {"choices":[{"delta":{"content":"[G]ok"}}]}""",
            "data: [DONE]");
        var handler = new FakeHandler(_ => Task.FromResult(Sse(HttpStatusCode.OK, sse)));
        var client = NewClient(handler);

        var result = await client.ConvertStreamingAsync(Settings(), "s", "u");
        Assert.Equal("[G]ok", result);
    }

    [Fact]
    public async Task ConvertAsync_EmptyChoicesArray_ThrowsLlmException_NotIndexOutOfRange()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [] }""")));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertAsync(Settings(), "s", "u"));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertAsync_NonStringContentNode_ThrowsLlmException_NotInvalidOperation()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            { "choices": [ { "message": { "content": { "nested": true } } } ] }
            """)));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertAsync(Settings(), "s", "u"));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ST-58 test double: a content stream that delivers one chunk then goes silent
    /// (stalls on the CALLER's token — a provider that accepts the request and hangs).
    /// Delivered via CreateContentReadStreamAsync so the client reads this stream directly;
    /// the HttpContent serialization pipe would drop the per-read stall token.
    /// </summary>
    private sealed class StallAfterFirstRead : MemoryStream
    {
        private int _reads;
        public StallAfterFirstRead()
            : base(Encoding.UTF8.GetBytes("""data: {"choices":[{"delta":{"content":"partial"}}]}""" + "\n")) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _reads) > 1)
            {
                await Task.Delay(Timeout.Infinite, ct); // silent provider: stalls until the read token fires
                return 0;
            }
            return base.ReadAsync(buffer, ct).Result;
        }
    }

    private sealed class RawStreamContent : HttpContent
    {
        private readonly Stream _stream;
        public RawStreamContent(Stream stream) => _stream = stream;
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
            => stream.FlushAsync(ct);
#pragma warning disable SYSLIB0026 // the two-arg overload is abstract in this TFM — must be overridden
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.FlushAsync();
#pragma warning restore SYSLIB0026
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    [Fact]
    public async Task ConvertStreamingAsync_MidStreamStall_ThrowsLlmException()
    {
        // ST-58: with ResponseHeadersRead the HttpClient timeout stops applying once headers
        // are in — a provider that goes silent mid-generation used to hang the serial LLM
        // queue forever. Every SSE read now has its own budget (the ctor's stallTimeout).
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new RawStreamContent(new StallAfterFirstRead())
        }));
        using var http = new HttpClient(handler);
        var client = new LlmClient(http, stallTimeout: TimeSpan.FromMilliseconds(400));

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ConvertStreamingAsync(Settings(), "s", "u"));
        Assert.Contains("stalled", ex.Message);
        Assert.Contains("7 generated characters", ex.Message); // reports how much was generated before the stall
    }

    // --- hint extraction -------------------------------------------------------

    [Theory]
    [InlineData("""{ "error": { "message": "too long message " } }""", "message")]
    [InlineData("""{ "error": "plain error" }""", "plain error")]
    [InlineData("""{ "message": "top-level" }""", "top-level")]
    public static void ExtractHint_ParsesCommonShapes(string body, string expected)
        => Assert.Contains(expected, LlmClient.ExtractHint(body));

    [Fact]
    public static void ExtractHint_TruncatesLongBodies()
    {
        var body = "{ \"error\": \" " + new string('x', 500) + " \" }";
        var hint = LlmClient.ExtractHint(body);
        Assert.True(hint!.Length <= 201);
        Assert.EndsWith("…", hint);
    }
}
