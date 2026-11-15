using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// ST-30: LLM settings editable from the Settings page (no .env / restart needed).
///
///   GET  /api/settings/llm          → { isAdmin, global, mine, effective }
///   PUT  /api/settings/llm/mine     → upsert (or delete) the current user's override row
///   PUT  /api/settings/llm/global   → admin only; upsert (or delete) the band-wide default row
///   POST /api/settings/llm/test     → GET {base}/models probe; 200 { ok, models? } or { ok:false, error }
///
/// All [Authorize] via the /api group; errors use the { error: { code, message, fields? } } shape.
/// </summary>
public static class LlmSettingsEndpoints
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Models the probe reports back — a hostile endpoint can stream an unbounded list.</summary>
    private const int MaxProbeModels = 100;

    public static RouteGroupBuilder MapLlmSettingsEndpoints(this RouteGroupBuilder api)
    {
        var settings = api.MapGroup("settings/llm");

        settings.MapGet("", async (HttpContext http, ApplicationDbContext db, ILlmSettingsProvider provider) =>
            Results.Ok(await PayloadAsync(http, db, provider)));

        settings.MapPut("/mine", async (LlmSettingsUpdateRequest request, HttpContext http, ApplicationDbContext db, ILlmSettingsProvider provider, ILlmEndpointPolicy policy) =>
        {
            var userId = CurrentUserId(http);
            if (userId is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Authentication required.");

            var validation = Validate(request, policy);
            if (validation is not null)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid LLM settings.", validation);

            await SaveRowAsync(db, userId, request);
            return Results.Ok(await PayloadAsync(http, db, provider));
        });

        settings.MapPut("/global", async (LlmSettingsUpdateRequest request, HttpContext http, ApplicationDbContext db, ILlmSettingsProvider provider, ILlmEndpointPolicy policy) =>
        {
            var userId = CurrentUserId(http);
            if (userId is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Authentication required.");
            if (!await Authz.IsAdminAsync(http))
                return Error(StatusCodes.Status403Forbidden, "forbidden", "Only the admin account can change the band-wide LLM default.");

            var validation = Validate(request, policy);
            if (validation is not null)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid LLM settings.", validation);

            await SaveRowAsync(db, null, request);
            return Results.Ok(await PayloadAsync(http, db, provider));
        });

        // Probe: 200 with ok:false on failure — the UI shows the result inline rather than as an ApiError.
        // ST-65: named IHttpClientFactory client (was a `new HttpClient` per click — socket churn).
        // SSRF: this URL is member input fetched server-side, so (a) the deployment policy
        // refuses private/internal targets, (b) the
        // named client follows no redirects, (c) the response is size-capped, and (d) failure
        // text never names the host, the port, ex.Message or the raw upstream body — the detail
        // goes to the log. The model list is bounded, and the list/hint that DO come back are the
        // caller's own endpoint's data, which is the point of the probe.
        settings.MapPost("/test", async (
            LlmTestRequest request, IHttpClientFactory httpFactory, ILlmEndpointPolicy policy,
            ILoggerFactory logFactory) =>
        {
            var log = logFactory.CreateLogger("LlmProbe");
            if (string.IsNullOrWhiteSpace(request.BaseUrl))
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Provide a base URL to test.");

            // Same normalization as conversions: /v1 is appended when missing.
            var baseUrl = LlmSettingsProvider.NormalizeBaseUrl(request.BaseUrl)!;
            if (!LlmUrlGuard.IsAllowedEndpoint(baseUrl, policy))
                return Refused();

            var http = httpFactory.CreateClient("llm-test");
            try
            {
                var probe = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
                if (!string.IsNullOrWhiteSpace(request.ApiKey) && !string.Equals(request.ApiKey, "not-needed", StringComparison.OrdinalIgnoreCase))
                    probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

                using var response = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead);
                var body = await HttpReading.ReadCappedAsStringAsync(response, InputLimits.MaxResponseProbeBytes);

                if (!response.IsSuccessStatusCode)
                {
                    var hint = LlmClient.ExtractHint(body);
                    var bounced = (int)response.StatusCode is >= 300 and < 400 ? " (redirects are not followed)" : "";
                    return Results.Json(new { ok = false, error = $"The server returned {(int)response.StatusCode}{bounced}{(hint is null ? "" : $": {hint}")}." });
                }

                var models = new List<string>();
                try
                {
                    var doc = JsonNode.Parse(body);
                    if (doc?["data"] is JsonArray arr)
                        foreach (var item in arr)
                            if (item?["id"] is JsonValue id && id.GetValue<string>() is { } name)
                            {
                                models.Add(name);
                                if (models.Count >= MaxProbeModels) break; // a hostile endpoint can stream thousands
                            }
                }
                catch (JsonException)
                {
                    // Reachable but no parseable model list — still a success.
                }
                return Results.Ok(new { ok = true, models });
            }
            catch (TaskCanceledException ex)
            {
                log.LogDebug(ex, "LLM probe timed out for {Target}", baseUrl);
                return Results.Json(new { ok = false, error = "Timed out after 10s reaching that endpoint." });
            }
            catch (InvalidOperationException ex)
            {
                // The response broke the size cap.
                log.LogDebug(ex, "LLM probe response rejected for {Target}", baseUrl);
                return Results.Json(new { ok = false, error = "That endpoint returned an oversized response." });
            }
            catch (HttpRequestException ex)
            {
                // ex.Message carries host and port ("Connection refused 127.0.0.1:5000") — log only.
                log.LogDebug(ex, "LLM probe failed for {Target}", baseUrl);
                return Results.Json(new { ok = false, error = "Could not reach that endpoint. Check the address and that the server is running." });
            }
        });

        return api;
    }

    /// <summary>The policy refusal, phrased the same for the probe and the saved-row paths.</summary>
    private static IResult Refused()
        => Error(StatusCodes.Status400BadRequest, "endpoint-refused",
            "That endpoint addresses a private or internal host, which this server's network policy forbids. " +
            "Set LLM_ALLOW_PRIVATE_NETWORK=true on a private instance, or use a public endpoint.");

    /// <summary>The GET/PUT response: both stored rows (masked) + the merged effective settings.</summary>
    private static async Task<object> PayloadAsync(HttpContext http, ApplicationDbContext db, ILlmSettingsProvider provider)
    {
        var userId = CurrentUserId(http);
        var userRow = userId is null ? null : await db.LlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId);
        var globalRow = await db.LlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == null);
        var effective = await provider.ResolveAsync(userId);

        return new
        {
            isAdmin = await Authz.IsAdminAsync(http),
            global = ToDto(globalRow),
            mine = ToDto(userRow),
            effective = new
            {
                baseUrl = effective.BaseUrl,
                model = effective.Model,
                keySet = !string.IsNullOrWhiteSpace(effective.ApiKey),
                maxTokens = effective.MaxTokens,
                source = effective.Source
            }
        };
    }

    /// <summary>Upsert the row for userId (null = global); an all-empty result deletes it.</summary>
    private static async Task SaveRowAsync(ApplicationDbContext db, string? userId, LlmSettingsUpdateRequest request)
    {
        var baseUrl = Clean(request.BaseUrl);
        var model = Clean(request.Model);
        var maxTokens = request.MaxTokens is > 0 ? request.MaxTokens.Value : 0;

        var row = await db.LlmSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        // The key is masked in responses, so the form cannot echo it back: null = keep what is
        // stored, "" (explicit) = remove it.
        var apiKey = request.ApiKey is null ? row?.ApiKey : Clean(request.ApiKey);

        if (row is null && (baseUrl is not null || model is not null || apiKey is not null || maxTokens > 0))
            db.LlmSettings.Add(new LlmSettings { UserId = userId, BaseUrl = baseUrl, Model = model, ApiKey = apiKey, MaxTokens = maxTokens });
        else if (row is not null)
        {
            if (baseUrl is null && model is null && apiKey is null && maxTokens == 0)
                db.LlmSettings.Remove(row); // all-empty save = "clear my settings"
            else
            {
                row.BaseUrl = baseUrl;
                row.Model = model;
                row.ApiKey = apiKey;
                row.MaxTokens = maxTokens;
            }
        }
        await db.SaveChangesAsync();
    }

    private static Dictionary<string, string?>? Validate(LlmSettingsUpdateRequest request, ILlmEndpointPolicy policy)
    {
        var fields = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            if (!Uri.TryCreate(request.BaseUrl.Trim(), UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                fields["baseUrl"] = "Base URL must be an absolute http(s) URL, e.g. http://127.0.0.1:1234/v1";
            // SSRF: refuse to STORE a target the policy forbids — the fetch-time check in
            // LlmClient covers a policy closed after the row was written; this one keeps the
            // Settings page honest instead of saving a row that fails at conversion time.
            else if (!LlmUrlGuard.IsAllowedEndpoint(request.BaseUrl, policy))
                fields["baseUrl"] =
                    "This server's network policy forbids a private or internal endpoint " +
                    "(LLM_ALLOW_PRIVATE_NETWORK is off). Use a public endpoint, or turn the flag on for a private instance.";
        }
        if (request.MaxTokens is <= 0 or > 1_000_000)
            fields["maxTokens"] = "Max tokens must be between 1 and 1,000,000.";
        return fields.Count > 0 ? fields : null;
    }

    private static LlmSettingsDto? ToDto(LlmSettings? row) => row is null
        ? null
        : new LlmSettingsDto(row.BaseUrl, row.Model, !string.IsNullOrWhiteSpace(row.ApiKey), row.MaxTokens);

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? CurrentUserId(HttpContext http)
        => http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    // ST-61: the old claim-based IsAdmin is gone — the gate loads the user row (Authz.IsAdminAsync),
    // so a DB demotion bites on the next request, not up to 30 days later.

    private static IResult Error(int statusCode, string code, string message, Dictionary<string, string?>? fields = null)
        => Results.Json(new { error = new { code, message, fields } }, statusCode: statusCode, options: Json);
}
