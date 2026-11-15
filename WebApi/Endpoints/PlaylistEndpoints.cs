using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// /api/playlists endpoints (ST-10). The /api group is RequireAuthorization by default (see
/// Program.cs), so no explicit [Authorize] is needed. A setlist is just a playlist with
/// IsSetlist=true — one entity, no separate concept. Errors use the { error: { code, message,
/// fields? } } shape. Positions are 1-based and contiguous.
/// </summary>
public static class PlaylistEndpoints
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapPlaylistEndpoints(this RouteGroupBuilder api)
    {
        var playlists = api.MapGroup("playlists");

        // GET /api/playlists?category=<collectionCategory>&search=&tag=  (ST-106: tag = exact
        // setlist tag, the collection retirement; category stays for the legacy page)
        playlists.MapGet("/", async (
            ApplicationDbContext db,
            string? category,
            string? search,
            string? tag) =>
        {
            var query = db.Playlists.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(category))
            {
                // Case-insensitive exact match on the owning collection's category.
                query = query.Where(p => p.Collection != null &&
                    EF.Functions.ILike(p.Collection.Category, EscapeLike(category.Trim()), "\\"));
            }
            if (!string.IsNullOrWhiteSpace(tag))
            {
                var tagIds = await db.Tags.AsNoTracking()
                    .Where(t => t.Kind == TagKind.Tag && t.NormalizedName == tag.Trim().ToLowerInvariant())
                    .Select(t => t.Id)
                    .ToListAsync();
                query = query.Where(p => db.PlaylistTagLinks.Any(l => l.PlaylistId == p.Id && tagIds.Contains(l.TagId)));
            }
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(p => EF.Functions.ILike(p.Name, LikePattern(search.Trim()), "\\"));

            var items = await query
                .OrderBy(p => p.Name)
                .ThenBy(p => p.Id)
                .Select(p => new PlaylistDto(
                    p.Id, p.Name, p.Description, p.CollectionId, p.IsSetlist, p.GigDate,
                    db.PlaylistSongs.Count(ps => ps.PlaylistId == p.Id),
                    p.CreatedAt, p.UpdatedAt))
                .ToListAsync();
            var tagMap = await TagWriter.NamesByPlaylistAsync(db, items.Select(i => i.Id).ToList());
            return Results.Ok(items
                .Select(i => i with { Tags = tagMap.GetValueOrDefault(i.Id) ?? [] })
                .ToList());
        });

        // POST /api/playlists -> 201
        playlists.MapPost("/", async (CreatePlaylistRequest request, ApplicationDbContext db) =>
        {
            var fields = new Dictionary<string, string?>();
            if (string.IsNullOrWhiteSpace(request.Name))
                fields["name"] = "Name is required.";
            var ok = TryGigDate(request.GigDate, out var gigDate);
            if (!ok) fields["gigDate"] = "Gig date must be a date (yyyy-MM-dd).";
            if (fields.Count > 0)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.", fields);
            if (request.CollectionId is { } cid && !await db.Collections.AnyAsync(c => c.Id == cid))
                return Error(StatusCodes.Status404NotFound, "not-found", "Collection not found.");

            var now = DateTime.UtcNow;
            var playlist = new Playlist
            {
                Id = Guid.NewGuid(),
                Name = request.Name!.Trim(),
                Description = Clean(request.Description),
                CollectionId = request.CollectionId,
                IsSetlist = request.IsSetlist ?? false,
                GigDate = gigDate,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Playlists.Add(playlist);
            // ST-106: setlist tags (single save: the playlist insert orders before its links).
            await TagWriter.ReplaceForPlaylistAsync(db, playlist.Id, request.Tags ?? []);
            await db.SaveChangesAsync();
            var createdTags = await TagWriter.NamesByPlaylistAsync(db, new[] { playlist.Id });
            return Results.Json(
                ToDto(playlist, 0) with { Tags = createdTags.GetValueOrDefault(playlist.Id) ?? [] },
                statusCode: StatusCodes.Status201Created);
        });

        // GET /api/playlists/{id} -> PlaylistDetailDto (songs ordered by Position)
        playlists.MapGet("/{id:guid}", async (Guid id, ApplicationDbContext db) =>
        {
            var playlist = await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (playlist is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Playlist not found.");

            var songIds = await db.PlaylistSongs
                .Where(ps => ps.PlaylistId == id)
                .OrderBy(ps => ps.Position)
                .Select(ps => ps.SongId)
                .ToListAsync();
            var songs = await db.Songs.AsNoTracking()
                .Where(s => songIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);
            var ordered = songIds.Where(songs.ContainsKey).Select(sid => ToSongDto(songs[sid])).ToList();

            var playlistTags = await TagWriter.NamesByPlaylistAsync(db, new[] { playlist.Id });
            return Results.Ok(new PlaylistDetailDto(
                playlist.Id, playlist.Name, playlist.Description, playlist.CollectionId,
                playlist.IsSetlist, playlist.GigDate, ordered.Count, playlist.CreatedAt, playlist.UpdatedAt,
                ordered, playlistTags.GetValueOrDefault(playlist.Id) ?? []));
        });

        // PUT /api/playlists/{id}
        playlists.MapPut("/{id:guid}", async (Guid id, UpdatePlaylistRequest request, HttpContext http, ApplicationDbContext db, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: renaming a setlist rewrites structure the band reads.
            if (await Authz.DenySharedDestroyAsync(http, policy, "rename a playlist") is { } denied)
                return denied;

            var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
            if (playlist is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Playlist not found.");

            var fields = new Dictionary<string, string?>();
            if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
                fields["name"] = "Name must not be empty.";
            var ok = TryGigDate(request.GigDate, out var gigDate);
            if (!ok) fields["gigDate"] = "Gig date must be a date (yyyy-MM-dd).";
            if (fields.Count > 0)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.", fields);
            if (request.CollectionId is { } cid && !await db.Collections.AnyAsync(c => c.Id == cid))
                return Error(StatusCodes.Status404NotFound, "not-found", "Collection not found.");

            if (request.Name is not null) playlist.Name = request.Name.Trim();
            if (request.Description is not null) playlist.Description = Clean(request.Description);
            if (request.CollectionId is not null) playlist.CollectionId = request.CollectionId;
            if (request.IsSetlist is { } iset) playlist.IsSetlist = iset;
            // Bug report #99: a present gigDate replaces the stored one ("" clears).
            if (request.GigDate is not null) playlist.GigDate = gigDate;
            // ST-106: a present tags list replaces the setlist's tags ("" entries drop).
            if (request.Tags is not null)
                await TagWriter.ReplaceForPlaylistAsync(db, id, request.Tags);
            playlist.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var songCount = await db.PlaylistSongs.CountAsync(ps => ps.PlaylistId == id);
            var updatedTags = await TagWriter.NamesByPlaylistAsync(db, new[] { id });
            return Results.Ok(ToDto(playlist, songCount) with { Tags = updatedTags.GetValueOrDefault(id) ?? [] });
        });

        // DELETE /api/playlists/{id} -> 204 (join rows cascade; songs survive)
        playlists.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ApplicationDbContext db, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: destroying shared structure is the admin's call on an
            // internet-facing instance.
            if (await Authz.DenySharedDestroyAsync(http, policy, "delete a playlist") is { } denied)
                return denied;

            var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
            if (playlist is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Playlist not found.");
            db.Playlists.Remove(playlist);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // POST /api/playlists/{id}/songs -> appends (position omitted) or inserts at position
        playlists.MapPost("/{id:guid}/songs", async (Guid id, AddSongRequest request, ApplicationDbContext db) =>
        {
            if (request.SongId is not { } songId)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.",
                    new Dictionary<string, string?> { ["songId"] = "songId is required." });
            if (!await db.Playlists.AnyAsync(p => p.Id == id))
                return Error(StatusCodes.Status404NotFound, "not-found", "Playlist not found.");
            if (!await db.Songs.AnyAsync(s => s.Id == songId))
                return Error(StatusCodes.Status404NotFound, "not-found", "Song not found.");
            if (await db.PlaylistSongs.AnyAsync(ps => ps.PlaylistId == id && ps.SongId == songId))
                return Error(StatusCodes.Status409Conflict, "already-member", "Song is already in this playlist.");

            var rows = await db.PlaylistSongs.Where(ps => ps.PlaylistId == id).ToListAsync();
            var currentIds = rows.OrderBy(ps => ps.Position).Select(ps => ps.SongId).ToList();
            var position = request.Position ?? currentIds.Count + 1;
            var newOrder = PlaylistOrdering.InsertAt(currentIds, songId, position);

            var rowMap = rows.ToDictionary(ps => ps.SongId);
            for (var i = 0; i < newOrder.Count; i++)
            {
                var sid = newOrder[i];
                if (sid == songId)
                    db.PlaylistSongs.Add(new PlaylistSong { PlaylistId = id, SongId = sid, Position = i + 1 });
                else
                    rowMap[sid].Position = i + 1;
            }
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // ST-63: the AnyAsync above is a check-then-insert — two concurrent adds both
                // pass it, and the second insert violates the composite PK. Answer 409, not 500.
                return Error(StatusCodes.Status409Conflict, "already-member", "Song is already in this playlist.");
            }
            var actualPosition = newOrder.IndexOf(songId) + 1;
            // MT-04: membership edits must be visible to the offline delta sync — the playlist's
            // UpdatedAt is its sync stamp, so every membership change bumps it.
            var memberPl = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
            if (memberPl is not null)
                memberPl.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Json(new { id, songId, position = actualPosition }, statusCode: StatusCodes.Status201Created);
        });

        // PUT /api/playlists/{id}/songs/reorder -> sets the full order (exactly current members)
        playlists.MapPut("/{id:guid}/songs/reorder", async (Guid id, ReorderRequest request, HttpContext http, ApplicationDbContext db, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: the order IS a setlist's structure, and it is what the band
            // is looking at live (SignalR-synced) — same class as renaming it.
            if (await Authz.DenySharedDestroyAsync(http, policy, "reorder a playlist") is { } denied)
                return denied;

            if (!await db.Playlists.AnyAsync(p => p.Id == id))
                return Error(StatusCodes.Status404NotFound, "not-found", "Playlist not found.");

            var rows = await db.PlaylistSongs.Where(ps => ps.PlaylistId == id).ToListAsync();
            var currentIds = rows.Select(ps => ps.SongId).ToList();
            var requested = request.SongIds ?? Array.Empty<Guid>();
            if (!PlaylistOrdering.IsExactReorder(currentIds, requested))
                return Error(StatusCodes.Status400BadRequest, "invalid-reorder",
                    "songIds must be exactly the playlist's current members.");

            var rowMap = rows.ToDictionary(ps => ps.SongId);
            for (var i = 0; i < requested.Length; i++)
                rowMap[requested[i]].Position = i + 1;
            // MT-04: reorder changes the setlist a device caches — bump the sync stamp.
            var reorderedPl = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
            if (reorderedPl is not null)
                reorderedPl.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // DELETE /api/playlists/{id}/songs/{songId} -> 204 (re-packs remaining positions)
        playlists.MapDelete("/{id:guid}/songs/{songId:guid}", async (Guid id, Guid songId, HttpContext http, ApplicationDbContext db, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: taking a song out of the setlist the band is playing from.
            // Adding one (POST) stays open — additive.
            if (await Authz.DenySharedDestroyAsync(http, policy, "remove a song from a playlist") is { } denied)
                return denied;

            var rows = await db.PlaylistSongs.Where(ps => ps.PlaylistId == id).ToListAsync();
            var row = rows.FirstOrDefault(ps => ps.SongId == songId);
            if (row is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Song is not in this playlist.");

            db.PlaylistSongs.Remove(row);
            var remaining = rows.Where(ps => ps.SongId != songId).OrderBy(ps => ps.Position).ToList();
            for (var i = 0; i < remaining.Count; i++)
                remaining[i].Position = i + 1;
            // MT-04: membership edit — bump the playlist's sync stamp (see add-song).
            var removePl = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
            if (removePl is not null)
                removePl.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return api;
    }

    /// <summary>Null/blank optional strings normalize to null; non-blank values are trimmed.</summary>
    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Escapes LIKE wildcards so a search/filter term is matched literally.</summary>
    private static string EscapeLike(string term)
        => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string LikePattern(string term)
        => "%" + EscapeLike(term) + "%";

    private static SongDto ToSongDto(Song s)
        => new(s.Id, s.Title, s.Artist, s.Album, s.Year, s.Key, s.Genre, s.Tempo, s.CapoFret, s.CreatedAt, s.UpdatedAt);

    private static PlaylistDto ToDto(Playlist p, int songCount)
        => new(p.Id, p.Name, p.Description, p.CollectionId, p.IsSetlist, p.GigDate, songCount, p.CreatedAt, p.UpdatedAt);

    /// <summary>
    /// Bug report #99: parse the gig date ("yyyy-MM-dd", stored as UTC midnight).
    /// Absent (null) → no change; "" → clear (null); invalid → false.
    /// (internal: unit-tested from WebApi.Tests, see ST-31 InternalsVisibleTo.)
    /// </summary>
    internal static bool TryGigDate(string? value, out DateTime? date)
    {
        date = null;
        if (value is null) return true;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (DateTime.TryParseExact(
                value.Trim(), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            date = parsed.Date;
            return true;
        }
        return false;
    }

    private static IResult Error(int statusCode, string code, string message, Dictionary<string, string?>? fields = null)
        => Results.Json(new { error = new { code, message, fields } }, statusCode: statusCode, options: Json);
}
