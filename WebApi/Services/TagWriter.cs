using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;

namespace WebApi.Services;

/// <summary>Per-song tag names grouped by kind, in the shape the DTOs render.</summary>
public sealed record SongTags(
    IReadOnlyList<string> Artists,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags)
{
    public static readonly SongTags Empty = new([], [], []);
}

/// <summary>
/// ST-106: the one write path for the tag tables — ensure-a-tag-row (normalised via
/// <see cref="TagName"/>) and (re)place links, plus the batch read shapes DTOs need.
/// Endpoints (song/playlist create+update), the import service, and sync-push all go
/// through here so the normalisation rule lives in exactly one place. The link tables
/// are the truth (AHC-1); the flat Artist/Genre columns are a write-only rollback
/// cache maintained by the callers for one release.
/// </summary>
public static class TagWriter
{
    /// <summary>Find or create the tag row for (Kind, name). Blank input → null.</summary>
    public static async Task<Tag?> EnsureAsync(ApplicationDbContext db, TagKind kind, string? raw)
    {
        var normalized = TagName.Normalized(kind, raw);
        if (normalized is null)
        {
            return null;
        }

        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Kind == kind && t.NormalizedName == normalized);
        if (tag is null)
        {
            tag = new Tag
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Name = TagName.Display(kind, raw)!,
                NormalizedName = normalized,
            };
            db.Tags.Add(tag);
        }

        return tag;
    }

    /// <summary>
    /// Replace the song's links for one kind with the given names (blank entries dropped,
    /// duplicates merged). A null <paramref name="names"/> is "not sent" — no change.
    /// The adds skip the "already linked" check: the deletions above are pending, and
    /// re-checking against the store would see the old rows on Postgres (the deletes
    /// are not sent yet) and skip the links that must survive the replace.
    /// </summary>
    public static async Task ReplaceForSongAsync(
        ApplicationDbContext db, Guid songId, TagKind kind, IReadOnlyList<string?>? names)
    {
        if (names is null)
        {
            return;
        }

        var current = await db.SongTagLinks
            .Where(l => l.SongId == songId)
            .Join(db.Tags, l => l.TagId, t => t.Id, (l, t) => new { Link = l, t.Kind })
            .Where(x => x.Kind == kind)
            .Select(x => x.Link)
            .ToListAsync();
        db.SongTagLinks.RemoveRange(current);

        var seen = new HashSet<Guid>();
        var position = 0;
        foreach (var raw in names)
        {
            position++;
            var tag = await EnsureAsync(db, kind, raw);
            if (tag is not null && seen.Add(tag.Id))
            {
                db.SongTagLinks.Add(new SongTagLink
                {
                    SongId = songId,
                    TagId = tag.Id,
                    Position = kind == TagKind.Artist ? position : 0,
                });
            }
        }
    }

    /// <summary>
    /// Add links for names not already linked (idempotent — the backfill's shape).
    /// Artists get Position 1..n (written order = display order); genres/tags stay 0
    /// and sort alphabetically in the read shape.
    /// </summary>
    public static async Task AddSongNamesAsync(
        ApplicationDbContext db, Guid songId, TagKind kind, IEnumerable<string?> names)
    {
        var existing = await db.SongTagLinks
            .Where(l => l.SongId == songId)
            .Select(l => l.TagId)
            .ToListAsync();
        var linked = existing.ToHashSet();

        var position = 0;
        foreach (var raw in names)
        {
            position++;
            var tag = await EnsureAsync(db, kind, raw);
            if (tag is not null && linked.Add(tag.Id))
            {
                db.SongTagLinks.Add(new SongTagLink
                {
                    SongId = songId,
                    TagId = tag.Id,
                    Position = kind == TagKind.Artist ? position : 0,
                });
            }
        }
    }

    /// <summary>Backfill shape for one song: artists from the split string, genre as-is.</summary>
    public static async Task ApplyFromFlatAsync(ApplicationDbContext db, Song song)
    {
        await AddSongNamesAsync(db, song.Id, TagKind.Artist, ArtistSplitter.Split(song.Artist));
        if (!string.IsNullOrWhiteSpace(song.Genre))
        {
            await AddSongNamesAsync(db, song.Id, TagKind.Genre, new[] { song.Genre });
        }
    }

    /// <summary>Replace a playlist's Kind=Tag links (the setlist grouping).</summary>
    public static async Task ReplaceForPlaylistAsync(
        ApplicationDbContext db, Guid playlistId, IReadOnlyList<string?>? names)
    {
        if (names is null)
        {
            return;
        }

        var current = await db.PlaylistTagLinks.Where(l => l.PlaylistId == playlistId).ToListAsync();
        db.PlaylistTagLinks.RemoveRange(current);

        var seen = new HashSet<Guid>();
        foreach (var raw in names)
        {
            var tag = await EnsureAsync(db, TagKind.Tag, raw);
            if (tag is not null && seen.Add(tag.Id))
            {
                db.PlaylistTagLinks.Add(new PlaylistTagLink { PlaylistId = playlistId, TagId = tag.Id });
            }
        }
    }

    /// <summary>Batch read: song id → names per kind, for list endpoints (one query).</summary>
    public static async Task<Dictionary<Guid, SongTags>> NamesBySongAsync(
        ApplicationDbContext db, IReadOnlyCollection<Guid> songIds)
    {
        if (songIds.Count == 0)
        {
            return [];
        }

        var rows = await db.SongTagLinks
            .Where(l => songIds.Contains(l.SongId))
            .Join(db.Tags, l => l.TagId, t => t.Id, (l, t) => new { l.SongId, l.Position, t.Name, t.Kind })
            .ToListAsync();

        // Artists keep the written list order (Position 1..n, legacy rows fall back to
        // name); genres and free tags sort alphabetically (their order carries no meaning).
        return rows
            .GroupBy(r => r.SongId)
            .ToDictionary(
                g => g.Key,
                g => new SongTags(
                    g.Where(r => r.Kind == TagKind.Artist)
                        .OrderBy(r => r.Position).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(r => r.Name).ToList(),
                    g.Where(r => r.Kind == TagKind.Genre).Select(r => r.Name)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    g.Where(r => r.Kind == TagKind.Tag).Select(r => r.Name)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()));
    }

    /// <summary>Batch read: playlist id → tag names.</summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<string>>> NamesByPlaylistAsync(
        ApplicationDbContext db, IReadOnlyCollection<Guid> playlistIds)
    {
        if (playlistIds.Count == 0)
        {
            return [];
        }

        var rows = await db.PlaylistTagLinks
            .Where(l => playlistIds.Contains(l.PlaylistId))
            .Join(db.Tags, l => l.TagId, t => t.Id, (l, t) => new { l.PlaylistId, t.Name })
            .ToListAsync();

        return rows
            .GroupBy(r => r.PlaylistId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList());
    }

    /// <summary>
    /// ST-106: the key from the chords (AHC-1). Derivation respects the manual-override
    /// flag; an unparseable chart leaves the stored key alone (no chords, no opinion).
    /// </summary>
    public static void ApplyDerivedKey(Song song)
    {
        var derived = KeyAnalyzer.Analyze(song.ChordProText);
        song.KeyConfidence = derived?.Confidence;
        if (derived is not null && !song.KeyOverride)
        {
            song.Key = derived.Key;
        }
    }
}
