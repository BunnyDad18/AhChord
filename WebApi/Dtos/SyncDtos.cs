namespace WebApi.Dtos;

/// <summary>
/// MT-04: one payload that lets an offline client cache the whole library.
/// <see cref="Songs"/> carries full detail (ChordPro text included) for songs changed
/// since <c>updatedSince</c> (all songs on the first sync); <see cref="SongIds"/> is the
/// complete id list so the client can prune local rows deleted on the server. Same pair
/// for playlists. Collections are small and always sent in full.
/// </summary>
public record SyncResponse(
    DateTime ServerTime,
    List<SongDetailDto> Songs,
    List<Guid> SongIds,
    List<PlaylistDetailDto> Playlists,
    List<Guid> PlaylistIds,
    List<CollectionDto> Collections);

/// <summary>
/// MT-05: one device-side song uploaded by POST /api/sync/push. <see cref="ClientId"/> is the
/// cache's id; the server issues its own id and returns the mapping, so the client can re-key
/// the cache row and never prune what the push landed. Key/capo/tempo are the device metadata
/// the ChordPro header directives may not carry; a header directive still wins for key and capo.
/// </summary>
public record PushSong(
    Guid ClientId,
    string? Title,
    string? Artist,
    string? Album,
    int? Year,
    string? Key,
    string? Genre,
    int? Tempo,
    int? CapoFret,
    string? ChordProText,
    string? SourceUrl);

/// <summary>
/// MT-05: a device-side playlist. <see cref="SongClientIds"/> are cache ids in position order,
/// resolved through the song push's mapping (ids the server never mapped are skipped).
/// Collections are not pushed — a restored playlist lands unfiled.
/// </summary>
public record PushPlaylist(
    Guid ClientId,
    string? Name,
    string? Description,
    bool IsSetlist,
    DateTime? GigDate,
    List<Guid> SongClientIds);

/// <summary>MT-05: POST /api/sync/push body — the rows the server has never seen, songs first.</summary>
public record PushLibraryRequest(List<PushSong>? Songs, List<PushPlaylist>? Playlists);

/// <summary>
/// MT-05: per-row outcome. <see cref="Error"/> is the server failure code (e.g. "not-chordpro")
/// when the row did not land — the client keeps its cache row and retries; <see cref="Created"/>
/// is false when the row already existed (dedup merge).
/// </summary>
public record PushSongResult(Guid ClientId, Guid Id, bool Created, string? Error);

/// <summary>MT-05: per-row playlist outcome (same semantics as <see cref="PushSongResult"/>).</summary>
public record PushPlaylistResult(Guid ClientId, Guid Id, bool Created, string? Error);

/// <summary>MT-05: POST /api/sync/push response — the clientId → serverId mapping per row.</summary>
public record PushLibraryResponse(
    DateTime ServerTime,
    List<PushSongResult> Songs,
    List<PushPlaylistResult> Playlists);
