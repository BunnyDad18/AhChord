namespace WebApi.Dtos;

/// <summary>ST-30: masked view of an LLM settings row — the API key is never returned in full.</summary>
public record LlmSettingsDto(string? BaseUrl, string? Model, bool KeySet, int MaxTokens);

/// <summary>
/// PUT /api/settings/llm/mine and /global. Empty fields clear that level (fall through to the
/// next) — except <see cref="ApiKey"/>: null = keep what is stored, "" = remove it, value = set.
/// </summary>
public record LlmSettingsUpdateRequest(string? BaseUrl, string? Model, string? ApiKey, int? MaxTokens);

/// <summary>POST /api/settings/llm/test — connectivity probe against the given endpoint.</summary>
public record LlmTestRequest(string? BaseUrl, string? Model, string? ApiKey);
