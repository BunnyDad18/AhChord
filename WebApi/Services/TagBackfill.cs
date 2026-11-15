using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebApi.Data;
using WebApi.Entities;

namespace WebApi.Services;

/// <summary>
/// ST-106: the one-shot backfill that fills the tag tables from the flat columns.
/// Runs at startup under Database:AutoMigrate (migrations in this repo stay pure
/// schema — a migration data pass cannot run on the InMemory test store, and the
/// suite must cover the pass, AHC-7), guarded by an AppMeta marker so it executes
/// once per database; every step is additionally idempotent on its own.
///
/// Steps: artist strings split into Artist tags (see <see cref="ArtistSplitter"/>),
/// genre strings canonicalised to title-case Genre tags, collections become Tag-kind
/// rows linked to their playlists (the setlist grouping), and every chart's Key is
/// re-derived from its chords (<see cref="KeyAnalyzer"/> — lyrics-only charts keep
/// their metadata key, there is nothing to derive from).
///
/// The flat columns are NOT rewritten: after this they keep their old values as the
/// write-only rollback cache (dropped in the S7 follow-up); the link tables are the
/// truth (AHC-1).
/// </summary>
public static class TagBackfill
{
    public const string MarkerKey = "tags-backfill-v1";

    public static void Run(ApplicationDbContext db, ILogger? log = null)
    {
        if (db.AppMeta.Any(m => m.Key == MarkerKey))
        {
            return;
        }

        var (tags, links, keys) = RunCore(db);

        db.AppMeta.Add(new AppMeta { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        db.SaveChanges();

        log?.LogInformation(
            "ST-106 tag backfill: {Tags} tag rows, {Links} links, {Keys} keys derived from chords.",
            tags, links, keys);
    }

    private static (int Tags, int Links, int Keys) RunCore(ApplicationDbContext db)
    {
        var newTags = 0;
        var newLinks = 0;
        var keysDerived = 0;

        var tagCache = new Dictionary<(TagKind Kind, string Normalized), Tag>();

        Tag? EnsureTag(TagKind kind, string? raw)
        {
            var normalized = TagName.Normalized(kind, raw);
            if (normalized is null)
            {
                return null;
            }

            if (!tagCache.TryGetValue((kind, normalized), out var tag))
            {
                tag = db.Tags.FirstOrDefault(t => t.Kind == kind && t.NormalizedName == normalized)
                    ?? AddTag(kind, raw!, normalized);
                tagCache[(kind, normalized)] = tag;
            }

            return tag;
        }

        Tag AddTag(TagKind kind, string raw, string normalized)
        {
            var tag = new Tag
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Name = TagName.Display(kind, raw)!,
                NormalizedName = normalized,
            };
            db.Tags.Add(tag);
            newTags++;
            return tag;
        }

        var songLinks = db.SongTagLinks
            .Select(l => new { l.SongId, l.TagId })
            .ToList()
            .Select(l => (l.SongId, l.TagId))
            .ToHashSet();

        var playlistLinks = db.PlaylistTagLinks
            .Select(l => new { l.PlaylistId, l.TagId })
            .ToList()
            .Select(l => (l.PlaylistId, l.TagId))
            .ToHashSet();

        var songs = db.Songs.ToList();

        // 1. Artist strings → Artist tags (split; " & " stays one chip on purpose).
        //    Position = written order (display order for the artist line).
        foreach (var song in songs)
        {
            var position = 0;
            foreach (var artist in ArtistSplitter.Split(song.Artist))
            {
                position++;
                var tag = EnsureTag(TagKind.Artist, artist);
                if (tag is not null && songLinks.Add((song.Id, tag.Id)))
                {
                    db.SongTagLinks.Add(new SongTagLink { SongId = song.Id, TagId = tag.Id, Position = position });
                    newLinks++;
                }
            }

            // 2. Genre string → one title-cased Genre tag (pop/Pop/POP become one "Pop").
            var genreTag = EnsureTag(TagKind.Genre, song.Genre);
            if (genreTag is not null && songLinks.Add((song.Id, genreTag.Id)))
            {
                db.SongTagLinks.Add(new SongTagLink { SongId = song.Id, TagId = genreTag.Id });
                newLinks++;
            }

            // 4. Key from the chords (AHC-1); a chart with no analysable chords keeps
            // the metadata key — there is no chord evidence to override it with.
            var derived = KeyAnalyzer.Analyze(song.ChordProText);
            if (derived is not null && song.Key != derived.Key)
            {
                song.Key = derived.Key;
                keysDerived++;
            }
        }

        // 3. Collections → Tag-kind rows linked to their playlists (ST-106 retirement).
        foreach (var collection in db.Collections.ToList())
        {
            var tag = EnsureTag(TagKind.Tag, collection.Name);
            if (tag is null)
            {
                continue;
            }

            foreach (var playlist in db.Playlists.Where(p => p.CollectionId == collection.Id).ToList())
            {
                if (playlistLinks.Add((playlist.Id, tag.Id)))
                {
                    db.PlaylistTagLinks.Add(new PlaylistTagLink { PlaylistId = playlist.Id, TagId = tag.Id });
                    newLinks++;
                }
            }
        }

        return (newTags, newLinks, keysDerived);
    }
}
