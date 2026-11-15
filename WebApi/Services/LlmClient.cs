using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebApi.Services;

/// <summary>Thrown when a conversion is requested but no LLM endpoint is configured.</summary>
public sealed class LlmNotConfiguredException()
    : Exception("LLM not configured. Set an LLM base URL in Settings → LLM conversion (or LLM_BASE_URL in .env).");

/// <summary>Thrown when the LLM endpoint is reachable but the conversion failed.</summary>
public sealed class LlmException(string message) : Exception(message);

/// <summary>
/// ST-14: minimal OpenAI-compatible chat client. One method: send (system, user) → text.
/// ST-30: settings are resolved per call via <see cref="ILlmSettingsProvider"/> (user row →
/// global row → .env), so the endpoint/model/key can change from the Settings page without a
/// restart. The Authorization header is set per request — never on the shared HttpClient —
/// because different users may use different keys.
/// ST-33: <see cref="ConvertStreamingAsync"/> adds SSE streaming (stream:true) with a
/// per-chunk callback so callers can report live progress while the model generates.
/// </summary>
public interface ILlmClient
{
    Task<string> ConvertAsync(LlmEffectiveSettings settings, string systemPrompt, string userContent, CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="ConvertAsync"/> but with `stream: true`: invokes <paramref name="onChunk"/>
    /// for every content delta as it arrives and returns the full accumulated text.
    /// </summary>
    Task<string> ConvertStreamingAsync(
        LlmEffectiveSettings settings, string systemPrompt, string userContent,
        Func<string, Task>? onChunk = null, CancellationToken ct = default);
}

public sealed class LlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly TimeSpan _stallTimeout;
    private readonly string? _defaultModel;
    private readonly ILlmEndpointPolicy _endpoints;

    /// <summary>
    /// ST-58: stallTimeout bounds each SSE read — with ResponseHeadersRead the HttpClient
    /// timeout stops applying once headers are in, so a provider that accepts the request
    /// and then goes silent used to hang the serial LLM queue indefinitely.
    /// ST-65: defaultModel is the config-provided fallback when a settings row has no model
    /// (was hardcoded "qwen3.8"); failure messages name the model that was asked for.
    /// SSRF: `endpoints` is the deployment policy — a BaseUrl that came from a SETTINGS ROW
    /// (a member's own input) is refused when the policy closes private ranges. The operator's
    /// own LLM_BASE_URL (source "env") is their configuration, not member input, so it is not
    /// policed. The default policy allows everything, so tests and callers are unaffected.
    /// </summary>
    public LlmClient(
        HttpClient http, TimeSpan? stallTimeout = null, string? defaultModel = null,
        ILlmEndpointPolicy? endpoints = null)
    {
        _http = http;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(120);
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim();
        _endpoints = endpoints ?? new LlmEndpointPolicy(allowPrivate: true);
    }

    /// <summary>The model a request for these settings will name (settings → config default).</summary>
    private string? EffectiveModel(LlmEffectiveSettings settings)
        => string.IsNullOrWhiteSpace(settings.Model) ? _defaultModel : settings.Model.Trim();

    /// <summary>
    /// POST {base}/chat/completions with temperature 0.2 and return the assistant text.
    /// Failures surface as <see cref="LlmException"/> with a readable reason (status + body hint),
    /// never as an unhandled crash. ST-58: the full body (send, read, parse) is wrapped, so the
    /// HttpClient timeout can't escape as a raw TaskCanceledException from the read, and the
    /// JSON is shape-guarded (providers emit `choices: []` chunks; `content` can be a non-string).
    /// </summary>
    public async Task<string> ConvertAsync(LlmEffectiveSettings settings, string systemPrompt, string userContent, CancellationToken ct = default)
    {
        var request = BuildRequest(settings, systemPrompt, userContent, stream: false);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var body = await new StreamReader(stream).ReadToEndAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException(UpstreamFailure(settings, response.StatusCode, body));
            }

            JsonNode? doc;
            try
            {
                doc = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                throw new LlmException("The LLM response was not valid JSON.");
            }

            var content = ExtractChoiceContent(doc);
            if (string.IsNullOrWhiteSpace(content))
                throw new LlmException($"The LLM returned an empty response (model \"{EffectiveModel(settings) ?? "qwen3.8"}\") — it may have spent its token budget on reasoning; raise max_tokens or use a non-thinking model.");

            return content.Trim();
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested == false)
        {
            throw new LlmException("The LLM request timed out.");
        }
        catch (HttpRequestException)
        {
            // No host, no port, no ex.Message: this text reaches the import draft through
            // ChordProConversionJob's Fail(lastError) and the Settings page. The detail is in the log.
            throw new LlmException("Could not reach the configured LLM endpoint — check the base URL in Settings and that the server is running.");
        }
    }

    /// <summary>
    /// ST-33: streaming variant. POSTs with `stream: true` and reads the SSE body line by line;
    /// each `data:` chunk's `choices[0].delta.content` is appended to the result and passed to
    /// <paramref name="onChunk"/> as it arrives (so callers can push live progress). The stream
    /// ends at `data: [DONE]`. Same failure mapping as <see cref="ConvertAsync"/>.
    /// ST-58: the read loop is inside the timeout mapping, and `choices: []` / non-string-delta
    /// chunks (keep-alives, done markers — providers really do send these) are skipped, not fatal.
    /// </summary>
    public async Task<string> ConvertStreamingAsync(
        LlmEffectiveSettings settings, string systemPrompt, string userContent,
        Func<string, Task>? onChunk = null, CancellationToken ct = default)
    {
        var request = BuildRequest(settings, systemPrompt, userContent, stream: true);

        var sb = new System.Text.StringBuilder();
        try
        {
            // ResponseHeadersRead: start reading the SSE body as soon as headers arrive.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new LlmException(UpstreamFailure(settings, response.StatusCode, body));
            }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            using var stall = new CancellationTokenSource();
            while (true)
            {
                // ST-58: bound every read — HttpClient.Timeout stops applying once headers are
                // in (ResponseHeadersRead), so a provider that goes silent mid-generation used
                // to hang the serial LLM queue indefinitely. Each line read gets its own budget.
                stall.CancelAfter(_stallTimeout);
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(stall.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new LlmException(
                        $"The LLM stream stalled{(sb.Length > 0 ? $" after {sb.Length} generated characters" : "")} — no data for {(int)_stallTimeout.TotalSeconds}s.");
                }
                if (line is null)
                    break;

                // SSE: only `data:` lines carry payload; ignore comments (": keep-alive") and blanks.
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                var data = line["data:".Length..].Trim();
                if (data.Length == 0)
                    continue;
                if (data == "[DONE]")
                    break;

                JsonNode? doc;
                try
                {
                    doc = JsonNode.Parse(data);
                }
                catch (JsonException)
                {
                    throw new LlmException("The LLM stream contained invalid JSON.");
                }

                var delta = ExtractDeltaContent(doc);
                if (!string.IsNullOrEmpty(delta))
                {
                    sb.Append(delta);
                    if (onChunk is not null)
                        await onChunk(delta);
                }
            }
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested == false)
        {
            // ST-58: this now covers the whole read loop, not just SendAsync — the 300 s
            // client timeout firing mid-generation used to escape unmapped.
            throw new LlmException($"The LLM stream timed out{(sb.Length > 0 ? $" after {sb.Length} generated characters." : ".")}");
        }
        catch (HttpRequestException)
        {
            // No host, no port, no ex.Message: this text reaches the import draft through
            // ChordProConversionJob's Fail(lastError) and the Settings page. The detail is in the log.
            throw new LlmException("Could not reach the configured LLM endpoint — check the base URL in Settings and that the server is running.");
        }

        var content = sb.ToString();
        if (string.IsNullOrWhiteSpace(content))
            throw new LlmException($"The LLM returned an empty response (model \"{EffectiveModel(settings) ?? "qwen3.8"}\") — it may have spent its token budget on reasoning; raise max_tokens or use a non-thinking model.");

        return content.Trim();
    }

    /// <summary>
    /// ST-58: shape-guarded content extraction for a non-streaming completion. A bare
    /// `choices[0]` on an empty array throws ArgumentOutOfRangeException and `GetValue&lt;string&gt;`
    /// throws InvalidOperationException on a non-string node — both used to escape the
    /// JsonException-only catch and crash the Hangfire job (10× retry storm).
    /// Returns null for any shape we can't use; the caller maps that to a readable LlmException.
    /// </summary>
    private static string? ExtractChoiceContent(JsonNode? doc)
    {
        if (doc?["choices"] is not JsonArray choices || choices.Count == 0)
            return null;
        if (choices[0] is not JsonObject choice || choice["message"] is not JsonObject message)
            return null;
        return message["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>ST-58: streaming counterpart of <see cref="ExtractChoiceContent"/> (delta.content).</summary>
    private static string? ExtractDeltaContent(JsonNode? doc)
    {
        if (doc?["choices"] is not JsonArray choices || choices.Count == 0)
            return null;
        if (choices[0] is not JsonObject choice || choice["delta"] is not JsonObject delta)
            return null;
        return delta["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>Builds the chat/completions request; the auth header is per-request (ST-30).</summary>
    private HttpRequestMessage BuildRequest(
        LlmEffectiveSettings settings, string systemPrompt, string userContent, bool stream)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            throw new LlmNotConfiguredException();

        // SSRF: a BaseUrl that came from a SETTINGS ROW is member input, so it is checked
        // against the deployment policy here too — not only when it was saved (the policy can
        // be closed after the row was written, and the job is where the fetch happens). The
        // operator's own LLM_BASE_URL (source "env") is their configuration, not member input.
        if (settings.Source is "user" or "global"
            && !LlmUrlGuard.IsAllowedEndpoint(settings.BaseUrl, _endpoints))
            throw new LlmException(
                "The configured LLM endpoint is not allowed by this server's network policy " +
                "(LLM_ALLOW_PRIVATE_NETWORK is off): it addresses a private or internal host. " +
                "Use a public endpoint, or turn the flag on for a private instance.");

        var baseUrl = settings.BaseUrl.Trim().TrimEnd('/');
        var payload = new
        {
            model = EffectiveModel(settings) ?? "qwen3.8",
            temperature = 0.2,
            max_tokens = settings.MaxTokens > 0 ? settings.MaxTokens : 4096,
            stream,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        };

        // No `using`: the message (and its buffered body) must stay readable after SendAsync —
        // same observable behavior as the old PostAsJsonAsync flow; tests inspect the request.
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey) && !string.Equals(settings.ApiKey, "not-needed", StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
        return request;
    }

    /// <summary>
    /// Failure line for a non-2xx from the LLM endpoint. The upstream body is echoed ONLY when
    /// the endpoint came from the operator's own <c>LLM_BASE_URL</c> (source "env"): for a
    /// member-configured endpoint that body is text the member's server chooses, and it used to
    /// travel into the import draft (Fail(lastError)) and the Settings page — an echo channel
    /// for an endpoint picked by an attacker. The status code alone tells a user enough.
    /// </summary>
    private string UpstreamFailure(LlmEffectiveSettings settings, System.Net.HttpStatusCode status, string body)
    {
        var hint = settings.Source == "env" ? ExtractHint(body) : null;
        return $"The LLM endpoint returned {(int)status}{(hint is null ? "" : $": {hint}")} (model \"{EffectiveModel(settings) ?? "qwen3.8"}\")";
    }

    /// <summary>Pulls a short human-readable reason out of common error bodies (OpenAI/Ollama/LM Studio shapes).</summary>
    public static string? ExtractHint(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        string? msg = null;
        try
        {
            var doc = JsonNode.Parse(body);
            var errorNode = doc?["error"];
            if (errorNode is JsonObject errorObj && errorObj["message"] is JsonValue errorMsg)
                msg = errorMsg.GetValue<string>();
            else if (errorNode is JsonValue errorString)
                msg = errorString.GetValue<string>();
            if (msg is null && doc?["message"] is JsonValue topMsg)
                msg = topMsg.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON, or an unexpected shape — fall through to the raw-body hint.
        }
        var hint = msg ?? (body.Length > 200 ? body[..200] + "…" : body);
        return hint.Length > 200 ? hint[..200] + "…" : hint;
    }
}
