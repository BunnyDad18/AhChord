namespace WebApi.Dtos;

/// <summary>List-level song shape — no raw ChordPro text.</summary>
public record SongDto(
    Guid Id,
    string Title,
    string? Artist,
    string? Album,
    int? Year,
    string? Key,
    string? Genre,
    // ST-35 F3: approximate tempo (BPM), proposed by the metadata review job.
    int? Tempo,
    int CapoFret,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // ST-106: multi-valued artist/genre and free tags, from the link tables (the truth).
    IReadOnlyList<string>? Artists = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>Song row as returned by GET /api/songs/recent (adds the viewing timestamp).</summary>
public record RecentSongDto(
    Guid Id,
    string Title,
    string? Artist,
    string? Album,
    int? Year,
    string? Key,
    string? Genre,
    int? Tempo,
    int CapoFret,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime ViewedAt);

/// <summary>Full song including the raw ChordPro text (source of truth).</summary>
public record SongDetailDto(
    Guid Id,
    string Title,
    string? Artist,
    string? Album,
    int? Year,
    string? Key,
    string? Genre,
    int? Tempo,
    int CapoFret,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string ChordProText,
    string? SourceUrl,
    // ST-106: the tag lists (link tables are the truth) + key-derivation state.
    IReadOnlyList<string>? Artists = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Tags = null,
    bool KeyOverride = false,
    double? KeyConfidence = null);

/// <summary>POST /api/songs. <c>title</c> and <c>chordProText</c> are required.</summary>
public record CreateSongRequest(
    string? Title,
    string? Artist,
    string? Album,
    int? Year,
    string? Key,
    string? Genre,
    int? CapoFret,
    string? ChordProText,
    string? SourceUrl,
    // ST-52: tempo is settable on create like every other metadata field.
    int? Tempo = null,
    // ST-106: the tag lists. When sent, artists/genres write the link tables (the truth)
    // AND the flat Artist/Genre columns (one-release rollback cache); an absent Artist
    // string falls back to splitting the first names. Key: sent = a hand-set key
    // (override flag set); absent = derived from the chords.
    IReadOnlyList<string>? Artists = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// PUT /api/songs/{id}. All fields optional: fields present (non-null) replace the stored
/// values, absent/null fields are left unchanged.
/// </summary>
public record UpdateSongRequest(
    string? Title,
    string? Artist,
    string? Album,
    int? Year,
    string? Key,
    string? Genre,
    int? CapoFret,
    string? ChordProText,
    string? SourceUrl,
    // ST-52: tempo is manually editable like every other field (30–300 BPM).
    int? Tempo = null,
    // ST-106: a sent list (non-null) REPLACES that kind's links wholesale ("" entries drop);
    // absent = unchanged. Key: non-blank = hand-set (override on); "" = cleared (override off,
    // the chords take over); absent = unchanged. A changed ChordProText re-derives the key
    // when no override is set.
    IReadOnlyList<string>? Artists = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>ST-35 D4/D5: distinct filter values for the songs-list filter panel.</summary>
public record SongFacetsDto(
    IReadOnlyList<string> Artists,
    IReadOnlyList<string> Genres,
    IReadOnlyList<int> Decades,
    /// <summary>ST-39 C1: total song groups — the unfiltered list length. ST-106: title only.</summary>
    int Total,
    /// <summary>ST-106: the free-tag names (the tag chip row; artists/genres above come from the tag tables).</summary>
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// ST-35 F1: one row per song (title+artist, case-insensitive) with its versions collapsed.
/// <see cref="Latest"/> is the most recently created version — what a list-row click plays;
/// <see cref="Ids"/> lists every version, oldest first.
/// </summary>
public record SongGroupDto(
    string Title,
    string? Artist,
    int VersionCount,
    SongDto Latest,
    IReadOnlyList<Guid> Ids,
    /// <summary>ST-35 F3: pending metadata proposals on the latest version (drives the Review button).</summary>
    int PendingProposals);

/// <summary>
/// ST-46 (bug-56): a source file attached to a song — metadata only (no bytes). The raw bytes
/// are fetched separately from GET /api/source-files/{id} and rendered inline on the song page.
/// </summary>
public record SongSourceFileDto(
    Guid Id,
    Guid SongId,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTime CreatedAt);
