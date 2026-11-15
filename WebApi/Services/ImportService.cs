using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;
using WebApi.Models;

namespace WebApi.Services;

/// <summary>ST-13/ST-14 import failure with the HTTP mapping the endpoints must emit.</summary>
public sealed class ImportException(
    string message,
    string code,
    int statusCode,
    Dictionary<string, string?>? fields = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public Dictionary<string, string?>? Fields { get; } = fields;
}

/// <summary>
/// Pure import logic (ST-13), kept out of the endpoint so it is unit-testable in isolation:
/// decides whether parsed text is ChordPro at all, and resolves metadata with the locked
/// precedence — explicit body fields > ChordPro header directives > null.
/// <see cref="CreateSongAsync"/> is the single place a song is created from pasted ChordPro —
/// both the ST-13 endpoint and the ST-14 draft save route go through it.
/// </summary>
public static class ImportService
{
    /// <summary>
    /// Validates the text, resolves metadata, creates the Song (and playlist membership when
    /// requested) and saves. Throws <see cref="ImportException"/> with the exact code/status/
    /// fields the API must return — no partial state on any failure path.
    /// ST-49: returns <c>Duplicate = true</c> when a song with the same title + artist (case-insensitive)
    /// and identical normalized ChordPro text already exists — nothing new is created, and a requested
    /// playlist gets the existing song added to it (idempotent setlist building).
    /// </summary>
    public static async Task<(Song Song, bool Duplicate)> CreateSongAsync(
        ApplicationDbContext db,
        string? chordProText,
        string? title,
        string? artist,
        // ST-35 F2: optional body overrides for genre/year (header directives are the fallback).
        string? genre = null,
        int? year = null,
        // ST-53: album override — MusicBrainz's release title IS the album; there is no {album:} header.
        string? album = null,
        Guid? playlistId = null,
        string? sourceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(chordProText))
            throw new ImportException("Invalid request.", "validation-failed", 400,
                new() { ["chordProText"] = "ChordPro text is required." });

        // ST-63 (finding 5): bounded text — checked BEFORE parsing so an oversized chart never
        // reaches the parser. The import endpoints bypass the song-endpoint validators, so the
        // caps live here, at the single chokepoint where a song is created from pasted ChordPro.
        var limitFields = new Dictionary<string, string?>();
        InputLimits.AddFieldLimits(limitFields, title, artist, album, null, genre, chordProText, sourceUrl);
        if (limitFields.Count > 0)
            throw new ImportException("Invalid request.", "validation-failed", 400, limitFields);

        // ST-48: normalize common ChordPro variants (shorthand headers like {t:...}, section
        // aliases like {soc}, bare "Verse1:" headings) to canonical form before parsing — the
        // stored chart is clean ChordPro and renders identically everywhere.
        chordProText = ChordProNormalizer.Normalize(chordProText);

        var parsed = ChordProParser.Parse(chordProText);
        if (!IsChordPro(parsed))
            throw new ImportException(
                "The text does not look like ChordPro (no lyric lines or no chords found).",
                "not-chordpro", 400);

        var (resolvedTitle, resolvedArtist, key, capoFret, resolvedGenre, resolvedYear) =
            ResolveMetadata(title, artist, genre, year, parsed.Header);
        if (string.IsNullOrWhiteSpace(resolvedTitle))
            throw new ImportException("Invalid request.", "validation-failed", 400,
                new() { ["title"] = "Title is required — provide it in the body or as a {title: ...} header directive." });

        // The playlist must exist before anything is written (no partial state on failure).
        if (playlistId is { } plId && !await db.Playlists.AnyAsync(p => p.Id == plId))
            throw new ImportException("Playlist not found.", "not-found", 404);

        // ST-49: duplicate detection — same title + artist (case-insensitive) with identical
        // normalized content is the same chart. Re-imports become no-ops instead of piling up
        // copies; a requested playlist still gets the existing song added to it.
        var songTitle = resolvedTitle.Trim();
        var songArtist = Clean(resolvedArtist);
        var songText = chordProText.Trim();
        Song? existing = songArtist is null
            ? await db.Songs.FirstOrDefaultAsync(s => s.Title.ToLower() == songTitle.ToLower() && s.Artist == null && s.ChordProText == songText)
            : await db.Songs.FirstOrDefaultAsync(s => s.Title.ToLower() == songTitle.ToLower() && s.Artist != null && s.Artist.ToLower() == songArtist.ToLower() && s.ChordProText == songText);
        if (existing is not null)
        {
            // ST-53: backfill a missing album from the re-import's override — never overwrite an existing value.
            var cleanAlbum = Clean(album);
            if (cleanAlbum is not null && string.IsNullOrWhiteSpace(existing.Album))
                existing.Album = cleanAlbum;

            if (playlistId is { } dupPlId && !await db.PlaylistSongs.AnyAsync(ps => ps.PlaylistId == dupPlId && ps.SongId == existing.Id))
            {
                var position = await db.PlaylistSongs.CountAsync(ps => ps.PlaylistId == dupPlId);
                db.PlaylistSongs.Add(new PlaylistSong { PlaylistId = dupPlId, SongId = existing.Id, Position = position + 1 });
            }
            await db.SaveChangesAsync();
            return (existing, true);
        }

        var now = DateTime.UtcNow;
        var song = new Song
        {
            Id = Guid.NewGuid(),
            Title = songTitle,
            Artist = songArtist,
            Key = Clean(key),
            CapoFret = capoFret,
            // ST-35 F2: genre/year come from the body override or the {genre:}/{year:} header.
            Genre = Clean(resolvedGenre),
            Year = resolvedYear,
            // ST-53: album comes only from the body override (MusicBrainz's release title is the album).
            Album = Clean(album),
            ChordProText = songText,
            SourceUrl = Clean(sourceUrl),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Songs.Add(song);

        // ST-106: tags for the imported chart (artist-string split, title-cased genre)
        // and the chord-derived key — chords win (AHC-1); a chart with no analysable
        // chords keeps the resolved metadata key.
        await TagWriter.ApplyFromFlatAsync(db, song);
        TagWriter.ApplyDerivedKey(song);

        if (playlistId is { } id)
        {
            var position = await db.PlaylistSongs.CountAsync(ps => ps.PlaylistId == id);
            db.PlaylistSongs.Add(new PlaylistSong { PlaylistId = id, SongId = song.Id, Position = position + 1 });
        }

        await db.SaveChangesAsync();
        return (song, false);
    }

    /// <summary>Null/blank optional strings normalize to null; non-blank values are trimmed.</summary>
    internal static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// True when the parsed song has at least one lyric line and at least one chord somewhere.
    /// The parser (ST-07) never throws, so garbage input degrades to this check rather than a 500.
    /// </summary>
    public static bool IsChordPro(ParsedSong? parsed)
    {
        if (parsed is null)
            return false;
        var lineCount = parsed.Sections.Sum(s => s.Lines.Count);
        if (lineCount == 0)
            return false;
        return parsed.Sections.Any(s => s.Lines.Any(l => l.Chords.Count > 0));
    }

    /// <summary>
    /// Resolves (title, artist, key, capoFret, genre, year) with body > header > null precedence.
    /// Capo is parsed from the header's <c>{capo: N}</c> and clamped to 0–6; unparseable values
    /// fall back to 0 (as written). Year (ST-35 F2) is parsed from <c>{year: YYYY}</c>; out-of-range
    /// or unparseable values fall back to null.
    /// </summary>
    public static (string? Title, string? Artist, string? Key, int CapoFret, string? Genre, int? Year) ResolveMetadata(
        string? bodyTitle, string? bodyArtist, string? bodyGenre = null, int? bodyYear = null, Header? header = null)
    {
        string? Pick(string? body, string? fromHeader)
            => !string.IsNullOrWhiteSpace(body)
                ? body
                : (string.IsNullOrWhiteSpace(fromHeader) ? null : fromHeader);

        var title = Pick(bodyTitle, header?.Title);
        var artist = Pick(bodyArtist, header?.Artist);
        var key = string.IsNullOrWhiteSpace(header?.Key) ? null : header.Key;
        var genre = Pick(bodyGenre, header?.Genre);

        var capo = 0;
        if (!string.IsNullOrWhiteSpace(header?.Capo) && int.TryParse(header.Capo, out var parsed))
            capo = Math.Clamp(parsed, 0, 6);

        var year = bodyYear ?? ParseYear(header?.Year);

        return (title, artist, key, capo, genre, year);
    }

    /// <summary>ST-35 F2: a plausible release year (1000–2099) or null.</summary>
    private static int? ParseYear(string? value)
        => !string.IsNullOrWhiteSpace(value) && int.TryParse(value.Trim(), out var y) && y is > 1000 and < 2100
            ? y
            : null;
}
