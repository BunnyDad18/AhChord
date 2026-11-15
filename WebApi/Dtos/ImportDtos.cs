namespace WebApi.Dtos;

/// <summary>
/// POST /api/songs/import (ST-13). <c>chordProText</c> is required. Metadata precedence:
/// body fields > ChordPro header directives ({title:}, {artist:}, {key:}, {capo:}, {genre:},
/// {year:}) > null. When <c>playlistId</c> is given the new song is appended to that playlist.
/// </summary>
public record ImportSongRequest(
    string? ChordProText,
    string? Title,
    string? Artist,
    // ST-35 F2: optional overrides — the {genre:}/{year:} header directives are the fallback.
    string? Genre = null,
    int? Year = null,
    // ST-53: album override (MusicBrainz's release title IS the album; there is no {album:} header).
    string? Album = null,
    Guid? PlaylistId = null,
    string? SourceUrl = null);

/// <summary>
/// POST /api/import/convert (ST-14): start an in-app chord-page→ChordPro conversion from
/// pasted page text (<c>content</c> is required — the server never fetches URLs).
/// Returns the draft id immediately; poll GET /api/import/convert/{id}.
/// </summary>
public record ConvertRequest(string? Content);

/// <summary>POST /api/import/convert/{id}/save (ST-14): same shape as ST-13 — optional overrides + playlist.</summary>
public record SaveDraftRequest(
    string? Title,
    string? Artist,
    // ST-35 F2: optional overrides — the {genre:}/{year:} header directives are the fallback.
    string? Genre = null,
    int? Year = null,
    Guid? PlaylistId = null);
