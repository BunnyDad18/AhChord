namespace WebApi.Dtos;

/// <summary>
/// ST-106: one tag row for GET /api/tags — the name, its kind ("artist" | "genre" | "tag")
/// and live counts. Autocomplete asks for a kind with <c>q=</c>; the tag browser (the old
/// collections page) reads kind="tag" and shows both counts.
/// </summary>
public record TagDto(
    string Kind,
    string Name,
    int SongCount,
    int PlaylistCount);

/// <summary>POST /api/songs/{id}/detect-key result — the chord-derived key + its confidence.</summary>
public record DetectedKeyDto(string Key, double Confidence);
