using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;
using WebApi.Models;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// /api/songs endpoints (ST-06). The /api group is RequireAuthorization by default (see Program.cs),
/// so no explicit [Authorize] is needed. Errors use the { error: { code, message, fields? } } shape.
/// PUT semantics: fields present (non-null) replace the stored values; absent/null fields are unchanged.
/// DELETE cascades: the DbContext already removes the song's PlaylistSong and PlayHistory rows.
/// </summary>
public static class SongEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int DefaultRecentLimit = 10;
    private const int MaxRecentLimit = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapSongEndpoints(this RouteGroupBuilder api)
    {
        var songs = api.MapGroup("songs");

        songs.MapGet("/", async (
            ApplicationDbContext db,
            string? search,
            int page = 1,
            int pageSize = DefaultPageSize,
            // ST-35 D4/D5 → ST-106: filter panel — artist/genre/tag are exact tag-name
            // matches against the LINK TABLES, so "Matt Redman" finds every song he wrote,
            // including the five-artist ones. tag= repeats (AND-narrowing, collection-style).
            string? artist = null,
            string? genre = null,
            string[]? tag = null,
            int? decade = null,
            // ST-35 F1: collapse versions — one row per title with a count + latest version.
            bool grouped = false) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);

            var query = db.Songs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                // ST-106: search covers title, the flat artist cache, and every tag name.
                // Lower-cased LIKE/Contains (portable: the endpoint tests run on InMemory,
                // where EF.Functions.ILike cannot execute) over a precomputed id set, so no
                // lambda mixes provider functions with list membership.
                var needle = search.Trim().ToLowerInvariant();
                var pattern = LikePattern(needle);
                var textIds = await db.Songs.AsNoTracking()
                    .Where(s =>
                        EF.Functions.Like(s.Title.ToLower(), pattern, "\\") ||
                        EF.Functions.Like(s.Artist!.ToLower(), pattern, "\\"))
                    .Select(s => s.Id)
                    .ToListAsync();
                var nameTagIds = await db.Tags.AsNoTracking()
                    .Where(t => t.NormalizedName.Contains(needle))
                    .Select(t => t.Id)
                    .ToListAsync();
                var taggedIds = nameTagIds.Count == 0
                    ? []
                    : (await db.SongTagLinks.AsNoTracking()
                        .Where(l => nameTagIds.Contains(l.TagId))
                        .Select(l => l.SongId)
                        .ToListAsync()).ToList();
                var searchIds = textIds.Union(taggedIds).ToList();
                query = query.Where(s => searchIds.Contains(s.Id));
            }

            if (!string.IsNullOrWhiteSpace(artist))
            {
                var artistIds = await ExactTagIdsAsync(db, TagKind.Artist, artist);
                query = query.Where(s => db.SongTagLinks.Any(l => l.SongId == s.Id && artistIds.Contains(l.TagId)));
            }
            if (!string.IsNullOrWhiteSpace(genre))
            {
                var genreIds = await ExactTagIdsAsync(db, TagKind.Genre, genre);
                query = query.Where(s => db.SongTagLinks.Any(l => l.SongId == s.Id && genreIds.Contains(l.TagId)));
            }
            if (tag is { Length: > 0 })
            {
                foreach (var raw in tag.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    var tagIds = await ExactTagIdsAsync(db, TagKind.Tag, raw);
                    query = query.Where(s => db.SongTagLinks.Any(l => l.SongId == s.Id && tagIds.Contains(l.TagId)));
                }
            }
            if (decade is > 0)
            {
                var start = decade.Value - decade.Value % 10; // normalize e.g. 1975 -> 1970
                query = query.Where(s => s.Year >= start && s.Year < start + 10);
            }

            // ST-35 F1 → ST-106: grouped mode — one row per TITLE (artist is a list now,
            // title+artist would fragment multi-artist songs), versions collapsed.
            // The library is small (self-hosted band app), so group in memory over the metadata
            // projection; pagination applies to groups, not raw rows.
            if (grouped)
            {
                var rows = await query
                    .Select(s => new { s.Id, s.Title, s.Artist, s.Year, s.Key, s.Genre, s.Tempo, s.CapoFret, s.CreatedAt, s.UpdatedAt })
                    .ToListAsync();

                var groupTagMap = await TagWriter.NamesBySongAsync(db, rows.Select(r => r.Id).ToList());

                // ST-35 F3: pending metadata proposals per song (drives the Review button on rows).
                var pendingCounts = await db.MetadataProposals.AsNoTracking()
                    .Where(p => p.Status == "pending")
                    .GroupBy(p => p.SongId)
                    .Select(g => new { SongId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.SongId, x => x.Count);

                var groups = rows
                    .GroupBy(r => r.Title.Trim().ToLowerInvariant())
                    .Select(g =>
                    {
                        var latest = g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).First();
                        return new SongGroupDto(
                            latest.Title,
                            latest.Artist,
                            g.Count(),
                            ToDto(
                                new Song
                                {
                                    Id = latest.Id,
                                    Title = latest.Title,
                                    Artist = latest.Artist,
                                    Year = latest.Year,
                                    Key = latest.Key,
                                    Genre = latest.Genre,
                                    Tempo = latest.Tempo,
                                    CapoFret = latest.CapoFret,
                                    CreatedAt = latest.CreatedAt,
                                    UpdatedAt = latest.UpdatedAt
                                },
                                groupTagMap.GetValueOrDefault(latest.Id)),
                            g.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Select(r => r.Id).ToList(),
                            pendingCounts.TryGetValue(latest.Id, out var pc) ? pc : 0);
                    })
                    .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                return Results.Ok(groups);
            }

            var items = await query
                .OrderBy(s => s.Title)
                .ThenBy(s => s.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var tagMap = await TagWriter.NamesBySongAsync(db, items.Select(s => s.Id).ToList());
            return Results.Ok(items.Select(s => ToDto(s, tagMap.GetValueOrDefault(s.Id))).ToList());
        });

        // ST-35 C1: lightweight total for the dashboard columns (the list endpoint returns no total).
        songs.MapGet("/count", async (ApplicationDbContext db) =>
            Results.Ok(new { count = await db.Songs.CountAsync() }));

        // ST-35 F1: every version of this song (same title+artist, case-insensitive), oldest first.
        songs.MapGet("/{id:guid}/versions", async (Guid id, ApplicationDbContext db) =>
        {
            var song = await db.Songs.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (song is null)
                return Error(404, "not-found", "Song not found.");

            var versions = await db.Songs.AsNoTracking()
                .Where(s => EF.Functions.ILike(s.Title, ExactPattern(song.Title), "\\"))
                .Where(s => song.Artist == null
                    ? s.Artist == null
                    : EF.Functions.ILike(s.Artist!, ExactPattern(song.Artist), "\\"))
                .OrderBy(s => s.CreatedAt)
                .ThenBy(s => s.Id)
                .ToListAsync();

            return Results.Ok(versions.Select(s => ToDto(s)).ToList());
        });

        // ST-106: distinct artists/genres/tags for the filter panel, from the TAG TABLES
        // (the truth). Case-insensitive sort in memory — the table is tiny.
        songs.MapGet("/facets", async (ApplicationDbContext db) =>
        {
            var artists = await TagNamesAsync(db, TagKind.Artist);
            var genres = await TagNamesAsync(db, TagKind.Genre);
            var tags = await TagNamesAsync(db, TagKind.Tag);
            var decades = await db.Songs.AsNoTracking()
                .Where(s => s.Year != null)
                .Select(s => (int)(s.Year! / 10) * 10)
                .Distinct()
                .OrderBy(d => d)
                .ToListAsync();

            // ST-39 C1 → ST-106: total = distinct TITLES (the grouped list groups by title
            // now), so "N of TOTAL" stays honest.
            var titleRows = await db.Songs.AsNoTracking().Select(s => s.Title).ToListAsync();
            var total = CountDistinctTitleGroups(titleRows);

            return Results.Ok(new SongFacetsDto(artists, genres, decades, total, tags));
        });

        songs.MapGet("/{id:guid}", async (Guid id, int? capo, ApplicationDbContext db) =>
        {
            var song = await db.Songs.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (song is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Song not found.");
            // ST-08 / ST-39 E1 / ST-51 #8 / ST-77 (bug report #116): ?capo=N is the session TRANSPOSE
            // (-12…+12). The displayed shapes = chart shifted by (N − the song's saved capo): the capo
            // moves the grips DOWN so the key you sound stays chart key + N, and the transpose moves
            // both the shapes and the key. A G chart with capo 2 and no transpose shows F shapes ("Capo 2 ·
            // Key of G"); transpose +2 shows A shapes and reads "Key of A". Absent/0 = as written (with
            // capo 0). The stored chart is never mutated.
            if (capo is < -12 or > 12)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Transpose must be between -12 and +12.");

            var parsed = ChordProParser.Parse(song.ChordProText);
            var shift = ProjectionShift(capo, song.CapoFret);
            if (shift != 0)
                ChordTransposer.Transpose(parsed, shift);
            // The raw text (source of truth) is always unchanged; `parsed` is the read-time projection.
            var detailTags = await TagWriter.NamesBySongAsync(db, new[] { song.Id });
            return Results.Ok(new { song = ToDetailDto(song, detailTags.GetValueOrDefault(song.Id)), parsed = parsed });
        });

        // ST-09: /random is a literal segment, so it never collides with /{id:guid} (which is guid-constrained).
        songs.MapGet("/random", async (Guid? playlistId, ApplicationDbContext db) =>
        {
            List<Guid> ids;
            string emptyMessage;
            if (playlistId is { } plId)
            {
                if (!await db.Playlists.AnyAsync(p => p.Id == plId))
                    return Error(StatusCodes.Status404NotFound, "not-found", "Playlist not found.");
                ids = await db.PlaylistSongs
                    .Where(ps => ps.PlaylistId == plId)
                    .Select(ps => ps.SongId)
                    .Distinct()
                    .ToListAsync();
                emptyMessage = "This playlist has no songs.";
            }
            else
            {
                ids = await db.Songs.Select(s => s.Id).ToListAsync();
                emptyMessage = "No songs in the library.";
            }

            var picked = SongService.PickRandom(ids);
            if (picked is null)
                return Error(StatusCodes.Status404NotFound, "empty", emptyMessage);

            var song = await db.Songs.AsNoTracking().FirstOrDefaultAsync(s => s.Id == picked);
            if (song is null)
                return Error(StatusCodes.Status404NotFound, "empty", emptyMessage);
            return Results.Ok(ToDto(song));
        });

        songs.MapGet("/recent", async (HttpContext http, ApplicationDbContext db, int limit = DefaultRecentLimit) =>
        {
            var userId = CurrentUserId(http);
            var clamped = Math.Clamp(limit <= 0 ? DefaultRecentLimit : limit, 1, MaxRecentLimit);

            // ST-65: group / sort / Take on the server (was: load the user's FULL history,
            // tracked, then order in memory — the history table grows unbounded per listener).
            var top = await db.PlayHistory.AsNoTracking()
                .Where(h => h.UserId == userId)
                .GroupBy(h => h.SongId)
                .Select(g => new { SongId = g.Key, ViewedAt = g.Max(h => h.ViewedAt) })
                .OrderByDescending(x => x.ViewedAt)
                .Take(clamped)
                .ToListAsync();
            if (top.Count == 0)
                return Results.Ok(new List<RecentSongDto>());

            var orderedIds = top.Select(t => t.SongId).ToList();
            var songs = await db.Songs.AsNoTracking()
                .Where(s => orderedIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);
            var byId = top.ToDictionary(t => t.SongId, t => t.ViewedAt);
            // Preserve recency order; drop any ids whose song was deleted in the meantime.
            return Results.Ok(
                orderedIds
                    .Where(songs.ContainsKey)
                    .Select(id =>
                    {
                        var dto = ToDto(songs[id]);
                        var viewedAt = byId.TryGetValue(id, out var viewed) ? viewed : songs[id].UpdatedAt;
                        return new RecentSongDto(
                            dto.Id, dto.Title, dto.Artist, dto.Album, dto.Year, dto.Key, dto.Genre, dto.Tempo,
                            dto.CapoFret, dto.CreatedAt, dto.UpdatedAt, viewedAt);
                    })
                    .ToList());
        });

        songs.MapPost("/{id:guid}/view", async (Guid id, HttpContext http, ApplicationDbContext db) =>
        {
            if (!await db.Songs.AnyAsync(s => s.Id == id))
                return Error(StatusCodes.Status404NotFound, "not-found", "Song not found.");

            db.PlayHistory.Add(new PlayHistory
            {
                Id = Guid.NewGuid(),
                SongId = id,
                UserId = CurrentUserId(http),
                ViewedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        songs.MapPost("/", async (CreateSongRequest request, HttpContext http, ApplicationDbContext db, IBackgroundJobClient jobs) =>
        {
            var fields = new Dictionary<string, string?>();
            if (string.IsNullOrWhiteSpace(request.Title))
                fields["title"] = "Title is required.";
            if (string.IsNullOrWhiteSpace(request.ChordProText))
                fields["chordProText"] = "ChordPro text is required.";
            if (request.CapoFret is < 0 or > 6)
                fields["capoFret"] = "Capo fret must be between 0 and 6.";
            if (request.Year is < 1800 or > 2100)
                fields["year"] = "Year must be between 1800 and 2100.";
            // ST-52: same range as MetadataReviewJob.NormalizeValue for tempo.
            if (request.Tempo is < 30 or > 300)
                fields["tempo"] = "Tempo must be between 30 and 300 BPM.";
            // ST-63 (finding 5): bounded text fields (see AddFieldLimits).
            InputLimits.AddFieldLimits(fields, request.Title, request.Artist, request.Album, request.Key,
                request.Genre, request.ChordProText, request.SourceUrl);
            // ST-106: bounded tag lists.
            AddTagListLimits(fields, "artists", request.Artists);
            AddTagListLimits(fields, "genres", request.Genres);
            AddTagListLimits(fields, "tags", request.Tags);
            if (fields.Count > 0)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.", fields);

            var now = DateTime.UtcNow;
            var song = new Song
            {
                Id = Guid.NewGuid(),
                Title = request.Title!.Trim(),
                // ST-106: the flat columns are the one-release rollback cache — written from
                // the lists when the lists came in (the link tables below are the truth).
                Artist = Clean(request.Artist) ?? JoinNames(request.Artists),
                Album = Clean(request.Album),
                Year = request.Year,
                // Key: sent = hand-set (override on, imports/Lint proposals never touch it);
                // absent = derived from the chords below.
                Key = Clean(request.Key),
                KeyOverride = Clean(request.Key) is not null,
                Genre = Clean(request.Genre) ?? FirstOf(request.Genres),
                Tempo = request.Tempo,
                CapoFret = request.CapoFret ?? 0,
                ChordProText = request.ChordProText!.Trim(),
                SourceUrl = Clean(request.SourceUrl),
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Songs.Add(song);

            // ST-106: tag links (ensure-row + link, idempotent); an absent artists list
            // falls back to splitting the legacy flat string.
            await TagWriter.AddSongNamesAsync(db, song.Id, TagKind.Artist,
                request.Artists ?? ArtistSplitter.Split(request.Artist));
            await TagWriter.AddSongNamesAsync(db, song.Id, TagKind.Genre,
                request.Genres ?? (string.IsNullOrWhiteSpace(request.Genre) ? [] : new[] { request.Genre }));
            await TagWriter.AddSongNamesAsync(db, song.Id, TagKind.Tag, request.Tags ?? []);

            if (Clean(request.Key) is null)
            {
                TagWriter.ApplyDerivedKey(song);
            }

            await db.SaveChangesAsync();

            // ST-35 F3: new songs are scanned for missing metadata automatically (background, best effort).
            jobs.Enqueue<MetadataReviewJob>(job => job.ScanSongAsync(song.Id, CurrentUserId(http)));

            var createdTags = await TagWriter.NamesBySongAsync(db, new[] { song.Id });
            return Results.Json(ToDetailDto(song, createdTags.GetValueOrDefault(song.Id)), statusCode: StatusCodes.Status201Created);
        });

        songs.MapPut("/{id:guid}", async (Guid id, UpdateSongRequest request, ApplicationDbContext db) =>
        {
            var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
            if (song is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Song not found.");

            var fields = new Dictionary<string, string?>();
            if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
                fields["title"] = "Title must not be empty.";
            if (request.ChordProText is not null && string.IsNullOrWhiteSpace(request.ChordProText))
                fields["chordProText"] = "ChordPro text must not be empty.";
            if (request.CapoFret is < 0 or > 6)
                fields["capoFret"] = "Capo fret must be between 0 and 6.";
            if (request.Year is < 1800 or > 2100)
                fields["year"] = "Year must be between 1800 and 2100.";
            // ST-52: same range as MetadataReviewJob.NormalizeValue for tempo.
            if (request.Tempo is < 30 or > 300)
                fields["tempo"] = "Tempo must be between 30 and 300 BPM.";
            // ST-63 (finding 5): bounded text fields (nulls = not sent, skipped).
            InputLimits.AddFieldLimits(fields, request.Title, request.Artist, request.Album, request.Key,
                request.Genre, request.ChordProText, request.SourceUrl);
            // ST-106: bounded tag lists.
            AddTagListLimits(fields, "artists", request.Artists);
            AddTagListLimits(fields, "genres", request.Genres);
            AddTagListLimits(fields, "tags", request.Tags);
            if (fields.Count > 0)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.", fields);

            if (request.Title is not null)
                song.Title = request.Title.Trim();
            if (request.Album is not null)
                song.Album = Clean(request.Album);
            if (request.Year is not null)
                song.Year = request.Year;
            if (request.Tempo is not null)
                song.Tempo = request.Tempo;
            if (request.CapoFret is { } capo)
                song.CapoFret = capo;
            if (request.ChordProText is not null)
                song.ChordProText = request.ChordProText.Trim();
            if (request.SourceUrl is not null)
                song.SourceUrl = Clean(request.SourceUrl);

            // ST-106: sent lists REPLACE the kind's links (the truth); the flat columns
            // follow as the cache. A flat-string-only write (old callers) keeps the cache
            // in sync without touching the link tables.
            if (request.Artists is not null)
            {
                await TagWriter.ReplaceForSongAsync(db, song.Id, TagKind.Artist, request.Artists);
                song.Artist = JoinNames(request.Artists);
            }
            else if (request.Artist is not null)
            {
                song.Artist = Clean(request.Artist);
            }
            if (request.Genres is not null)
            {
                await TagWriter.ReplaceForSongAsync(db, song.Id, TagKind.Genre, request.Genres);
                song.Genre = FirstOf(request.Genres);
            }
            else if (request.Genre is not null)
            {
                song.Genre = Clean(request.Genre);
            }
            if (request.Tags is not null)
            {
                await TagWriter.ReplaceForSongAsync(db, song.Id, TagKind.Tag, request.Tags);
            }

            // ST-106 key semantics: non-blank Key = hand-set (override on, the chords
            // stop touching it); "" = cleared, derivation takes over immediately;
            // absent + text change = re-derive (an override key is left alone).
            if (request.Key is not null)
            {
                var key = Clean(request.Key);
                song.Key = key;
                song.KeyOverride = key is not null;
                if (key is null)
                {
                    TagWriter.ApplyDerivedKey(song);
                }
            }
            else if (request.ChordProText is not null)
            {
                TagWriter.ApplyDerivedKey(song);
            }

            song.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var updatedTags = await TagWriter.NamesBySongAsync(db, new[] { song.Id });
            return Results.Ok(ToDetailDto(song, updatedTags.GetValueOrDefault(song.Id)));
        });

        // ST-106: the editor's "detect from chords" action — re-derive the key from the
        // chart, clearing the manual override.
        songs.MapPost("/{id:guid}/detect-key", async (Guid id, ApplicationDbContext db) =>
        {
            var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
            if (song is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Song not found.");

            var derived = KeyAnalyzer.Analyze(song.ChordProText);
            if (derived is null)
                return Error(StatusCodes.Status422UnprocessableEntity, "no-chords",
                    "No analysable chords in this chart.");

            song.Key = derived.Key;
            song.KeyOverride = false;
            song.KeyConfidence = derived.Confidence;
            song.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new DetectedKeyDto(derived.Key, derived.Confidence));
        });

        songs.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ApplicationDbContext db, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: the most destructive shared write in the product — a chart
            // nobody can re-upload. Its attachment DELETE is gated for the same reason.
            if (await Authz.DenySharedDestroyAsync(http, policy, "delete a song") is { } denied)
                return denied;

            var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
            if (song is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Song not found.");

            db.Songs.Remove(song);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return api;
    }

    /// <summary>Null/blank optional strings normalize to null; non-blank values are trimmed.</summary>
    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>ST-106: ids of the exact (normalised) tag row(s) for a filter value.</summary>
    private static async Task<List<Guid>> ExactTagIdsAsync(ApplicationDbContext db, TagKind kind, string value)
        => await db.Tags.AsNoTracking()
            .Where(t => t.Kind == kind && t.NormalizedName == value.Trim().ToLowerInvariant())
            .Select(t => t.Id)
            .ToListAsync();

    /// <summary>ST-106: all names of one kind, sorted like the old in-memory facets did.</summary>
    private static async Task<List<string>> TagNamesAsync(ApplicationDbContext db, TagKind kind)
        => (await db.Tags.AsNoTracking()
                .Where(t => t.Kind == kind)
                .Select(t => t.Name)
                .ToListAsync())
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>ST-106: tag-list bounds — a list is bounded per item (meta-field cap) and in count.</summary>
    private static void AddTagListLimits(Dictionary<string, string?> fields, string name, IReadOnlyList<string>? list)
    {
        if (list is null)
        {
            return;
        }

        if (list.Count > 20)
        {
            fields[name] = $"{name} is limited to 20 values.";
            return;
        }

        if (list.Any(v => (v ?? "").Trim().Length > InputLimits.MaxMetaFieldLength))
        {
            fields[name] = $"{name} values are limited to {InputLimits.MaxMetaFieldLength:N0} characters.";
        }
    }

    /// <summary>ST-106: the flat-artist cache shape — names rejoined with commas (null when empty).</summary>
    private static string? JoinNames(IReadOnlyList<string>? names)
    {
        var parts = (names ?? []).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>ST-106: the flat-genre cache shape — the single column keeps the FIRST genre,
    /// in canonical display form (the link tables are the truth).</summary>
    private static string? FirstOf(IReadOnlyList<string>? names)
        => (names ?? []).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).FirstOrDefault() is { } first
            ? TagName.Display(TagKind.Genre, first)
            : null;

    /// <summary>
    /// ST-65 → ST-106: distinct title groups, keyed exactly like the grouped song list
    /// (trimmed + lowercased) — so the facets "total" never overcounts. ST-106: title
    /// only (artist is a list, title+artist would fragment multi-artist songs).
    /// </summary>
    internal static int CountDistinctTitleGroups(IEnumerable<string> titles)
        => titles
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .Count();

    // ST-63 (finding 5): text-field caps live in InputLimits (shared with the import service,
    // which is the other chokepoint that creates songs).

    /// <summary>The authenticated user's id from the JWT (the "sub" claim).</summary>
    private static string? CurrentUserId(HttpContext http)
        => http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>Escapes LIKE/ILIKE wildcards in user input so a search for "%" is literal.</summary>
    private static string LikePattern(string term)
        => "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>ST-35 F1: exact (no wildcards) ILIKE pattern — escapes LIKE metacharacters in stored values.</summary>
    private static string ExactPattern(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static SongDto ToDto(Song s, SongTags? tags = null)
        => new(s.Id, s.Title, s.Artist, s.Album, s.Year, s.Key, s.Genre, s.Tempo, s.CapoFret, s.CreatedAt, s.UpdatedAt,
            tags?.Artists ?? [], tags?.Genres ?? [], tags?.Tags ?? []);

    /// <summary>
    /// ST-77 (#116): the read-time projection shift for GET /{id}: the session
    /// transpose moves shapes AND key; the saved capo moves the shapes down —
    /// so the displayed chart shifts by (session transpose − saved capo).
    /// </summary>
    public static int ProjectionShift(int? sessionTranspose, int savedCapo) => (sessionTranspose ?? 0) - savedCapo;

    private static SongDetailDto ToDetailDto(Song s, SongTags? tags = null)
        => new(s.Id, s.Title, s.Artist, s.Album, s.Year, s.Key, s.Genre, s.Tempo, s.CapoFret, s.CreatedAt, s.UpdatedAt,
            s.ChordProText, s.SourceUrl,
            tags?.Artists ?? [], tags?.Genres ?? [], tags?.Tags ?? [], s.KeyOverride, s.KeyConfidence);

    private static IResult Error(int statusCode, string code, string message, Dictionary<string, string?>? fields = null)
        => Results.Json(new { error = new { code, message, fields } }, statusCode: statusCode, options: Json);
}
