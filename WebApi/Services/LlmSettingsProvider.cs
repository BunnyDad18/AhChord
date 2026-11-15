using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;

namespace WebApi.Services;

/// <summary>The LLM settings in effect after merging user row → global row → environment.</summary>
public sealed record LlmEffectiveSettings(
    string? BaseUrl,
    string? Model,
    string? ApiKey,
    int MaxTokens,
    /// <summary>Where the base URL came from: "user" | "global" | "env" | "none".</summary>
    string Source);

/// <summary>
/// ST-30: resolves LLM settings at call time so they can be edited from the Settings page.
/// Per-field merge — for each field the first non-empty value wins, in order:
/// the user's own row → the band-wide (global) row → .env (LLM_BASE_URL / LLM_MODEL / …).
/// </summary>
public interface ILlmSettingsProvider
{
    Task<LlmEffectiveSettings> ResolveAsync(string? userId, CancellationToken ct = default);

    /// <summary>
    /// ST-35 F3: resolution for band-wide background jobs — triggering user → global row →
    /// any configured row. In a self-hosted band app one member configures the LLM for
    /// everyone; a job must not just skip because the triggerer has no personal settings.
    /// </summary>
    Task<LlmEffectiveSettings> ResolveForJobAsync(string? userId, CancellationToken ct = default);
}

public sealed class LlmSettingsProvider : ILlmSettingsProvider
{
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;

    public LlmSettingsProvider(ApplicationDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<LlmEffectiveSettings> ResolveAsync(string? userId, CancellationToken ct = default)
    {
        var userRow = userId is null
            ? null
            : await _db.LlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var globalRow = await _db.LlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == null, ct);
        return Merge(userRow, globalRow, _config);
    }

    public async Task<LlmEffectiveSettings> ResolveForJobAsync(string? userId, CancellationToken ct = default)
    {
        var userRow = userId is null
            ? null
            : await _db.LlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var globalRow = await _db.LlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == null, ct);

        // Neither the triggerer nor a global row has an endpoint: fall back to any member's
        // configured row so band-wide jobs (metadata review) keep working.
        if (string.IsNullOrWhiteSpace(userRow?.BaseUrl) && string.IsNullOrWhiteSpace(globalRow?.BaseUrl))
        {
            var anyRow = await _db.LlmSettings.AsNoTracking()
                .Where(s => s.UserId != null)
                .FirstOrDefaultAsync(s => !string.IsNullOrEmpty(s.BaseUrl), ct);
            if (anyRow is not null)
            {
                // ST-65: WHOLE-ROW fallback — that row is one member's complete config
                // (endpoint + key + model travel together). Merging the triggerer's fields
                // on top used to aim their model choice at another member's endpoint/key.
                return Merge(null, anyRow, _config);
            }
        }

        return Merge(userRow, globalRow, _config);
    }

    /// <summary>Pure merge (unit-testable without a database): user → global → env per field.</summary>
    public static LlmEffectiveSettings Merge(LlmSettings? userRow, LlmSettings? globalRow, IConfiguration env)
    {
        var baseUrl = NormalizeBaseUrl(FirstNonEmpty(userRow?.BaseUrl, globalRow?.BaseUrl, Env(env, "LLM_BASE_URL")));
        var model = FirstNonEmpty(userRow?.Model, globalRow?.Model, Env(env, "LLM_MODEL"));
        var apiKey = FirstNonEmpty(userRow?.ApiKey, globalRow?.ApiKey, Env(env, "LLM_API_KEY"));
        var maxTokens = userRow is { MaxTokens: > 0 } ? userRow.MaxTokens
            : globalRow is { MaxTokens: > 0 } ? globalRow.MaxTokens
            : env.GetValue("LLM_MAX_TOKENS", 4096);

        // Source label follows the base URL — it decides whether conversion is possible at all.
        var source = baseUrl is null ? "none"
            : !string.IsNullOrWhiteSpace(userRow?.BaseUrl) ? "user"
            : !string.IsNullOrWhiteSpace(globalRow?.BaseUrl) ? "global"
            : "env";

        return new LlmEffectiveSettings(baseUrl, model, apiKey, maxTokens, source);
    }

    /// <summary>
    /// OpenAI-compatible base URLs conventionally end in /v1 (LM Studio, Ollama, OpenAI) —
    /// append it when missing so users can enter either form. Trailing slashes are trimmed.
    /// </summary>
    public static string? NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var trimmed = url.Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/v1";
    }

    private static string? Env(IConfiguration config, string key)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
