namespace WebApi.Dtos;

/// <summary>List-level playlist shape (with a live <c>songCount</c>).</summary>
public record PlaylistDto(
    Guid Id,
    string Name,
    string? Description,
    Guid? CollectionId,
    bool IsSetlist,
    DateTime? GigDate,
    int SongCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // ST-106: setlist tags (the collection retirement — a setlist carries several).
    IReadOnlyList<string>? Tags = null);

/// <summary>Full playlist including its songs in <c>Position</c> order.</summary>
public record PlaylistDetailDto(
    Guid Id,
    string Name,
    string? Description,
    Guid? CollectionId,
    bool IsSetlist,
    DateTime? GigDate,
    int SongCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<SongDto> Songs,
    // ST-106: setlist tags.
    IReadOnlyList<string>? Tags = null);

/// <summary>POST /api/playlists. <c>name</c> is required. <c>gigDate</c> is "yyyy-MM-dd" (bug report #99).</summary>
public record CreatePlaylistRequest(
    string? Name,
    string? Description,
    Guid? CollectionId,
    bool? IsSetlist,
    string? GigDate,
    // ST-106: setlist tags (free-text, autocomplete over existing).
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// PUT /api/playlists/{id}. All fields optional: present (non-null) values replace the stored
/// values, absent/null fields are left unchanged (same semantics as the song PUT).
/// <c>gigDate</c> "" clears the gig date (bug report #99).
/// </summary>
public record UpdatePlaylistRequest(
    string? Name,
    string? Description,
    Guid? CollectionId,
    bool? IsSetlist,
    string? GigDate,
    // ST-106: a sent list replaces the setlist's tags; absent = unchanged.
    IReadOnlyList<string>? Tags = null);

/// <summary>POST /api/playlists/{id}/songs. <c>songId</c> required; <c>position</c> (1-based) optional — omitted appends.</summary>
public record AddSongRequest(
    Guid? SongId,
    int? Position);

/// <summary>PUT /api/playlists/{id}/songs/reorder. <c>songIds</c> must be exactly the current members.</summary>
public record ReorderRequest(
    Guid[]? SongIds);
