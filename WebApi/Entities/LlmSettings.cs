namespace WebApi.Entities;

/// <summary>
/// ST-30: LLM endpoint settings, editable from the Settings page (no .env / restart needed).
/// One global row (<see cref="UserId"/> == null) is the band-wide default set by an admin;
/// one row per user is a personal override. Fields are merged per-field at call time —
/// resolution order: user row → global row → environment (LLM_BASE_URL / LLM_MODEL / …).
/// An all-empty row is deleted on save, so "no value" always falls through to the next level.
/// </summary>
public class LlmSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>null = the band-wide default row; otherwise the owning user's override.</summary>
    public string? UserId { get; set; }

    /// <summary>OpenAI-compatible base URL, e.g. http://127.0.0.1:1234/v1</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Model id as the server expects it, e.g. qwen/qwen3.8-27b</summary>
    public string? Model { get; set; }

    /// <summary>Bearer key for the endpoint (plaintext — self-hosted single-band app).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Completion-token cap (thinking models need a cap); 0 = use the next level / default.</summary>
    public int MaxTokens { get; set; }
}
