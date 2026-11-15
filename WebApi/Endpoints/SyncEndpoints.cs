using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;

namespace WebApi.Endpoints;

/// <summary>
/// MT-04: /api/sync — the offline cache payload. GET /api/sync?updatedSince=&lt;ISO&gt; returns
/// the full library (songs with ChordPro text, playlists with their songs, collections) on the
/// first call, and only rows changed since the stamp afterwards; the complete id lists let the
/// client prune what was deleted server-side. No schema change — the UpdatedAt columns exist.
/// The library is small (self-hosted band app), so the rows are loaded and assembled in memory,
/// the same way the grouped song list does (ST-35 F1).
/// </summary>
public static class SyncEndpoints
{
    public static RouteGroupBuilder MapSyncEndpoints(this RouteGroupBuilder api)
    {
        var sync = api.MapGroup("sync");

        sync.MapGet("/", async (string? updatedSince, ApplicationDbContext db) =>
        {
            // RoundtripKind keeps the client's UTC "Z" stamp in UTC; anything else is read as UTC.
            DateTime? since = ParseSince(updatedSince);

            var songs = await db.Songs.AsNoTracking().ToListAsync();
            var playlists = await db.Playlists.AsNoTracking().ToListAsync();
            var links = await db.PlaylistSongs.AsNoTracking().ToListAsync();
            var collections = await db.Collections.AsNoTracking().ToListAsync();

            var changedSongs = since is null
                ? songs
                : songs.Where(s => s.UpdatedAt > since.Value).ToList();
            var changedPlaylists = since is null
                ? playlists
                : playlists.Where(p => p.UpdatedAt > since.Value).ToList();

            return Results.Ok(Build(changedSongs, songs, changedPlaylists, playlists, links, collections, DateTime.UtcNow));
        });

        // MT-05: restore push — the device uploads the cache rows the server has never seen
        // (rebuilt/empty server, new pairing) before its client prunes, so the one-way pull
        // can never destroy the device's only copy. Per-row outcomes, never an all-or-nothing
        // 400: one broken chart must not fail the library. See SyncPushService.
        sync.MapPost("/push", async (PushLibraryRequest request, ApplicationDbContext db) =>
        {
            if ((request.Songs?.Count ?? 0) > Services.SyncPushService.MaxSongsPerRequest
                || (request.Playlists?.Count ?? 0) > Services.SyncPushService.MaxPlaylistsPerRequest)
                return Results.Json(
                    new
                    {
                        error = new
                        {
                            code = "too-many-rows",
                            message = $"A push carries at most {Services.SyncPushService.MaxSongsPerRequest} songs "
                                + $"and {Services.SyncPushService.MaxPlaylistsPerRequest} playlists — chunk the payload."
                        }
                    },
                    statusCode: StatusCodes.Status400BadRequest);

            return Results.Ok(await Services.SyncPushService.PushAsync(db, request));
        });

        return api;
    }

    private static DateTime? ParseSince(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();
        return null; // unparseable stamp → behave like a first full sync
    }

    /// <summary>
    /// Pure payload assembly (unit-tested in SyncEndpointsTests). Changed rows carry detail;
    /// the id lists always describe the complete library so the client can reconcile deletions.
    /// </summary>
    public static SyncResponse Build(
        IReadOnlyList<Song> changedSongs,
        IReadOnlyList<Song> allSongs,
        IReadOnlyList<Playlist> changedPlaylists,
        IReadOnlyList<Playlist> allPlaylists,
        IReadOnlyList<PlaylistSong> links,
        IReadOnlyList<Collection> collections,
        DateTime serverTime)
    {
        var songById = allSongs.ToDictionary(s => s.Id);

        // Playlists carry their full song list in position order (the detail-endpoint shape);
        // membership edits bump playlist.UpdatedAt, so a changed playlist means its songs changed.
        var linksByPlaylist = links
            .GroupBy(l => l.PlaylistId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Position).ToList());

        var playlists = changedPlaylists
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var ordered = linksByPlaylist.TryGetValue(p.Id, out var ls)
                    ? ls.Where(l => songById.ContainsKey(l.SongId)).Select(l => ToDto(songById[l.SongId])).ToList()
                    : new List<SongDto>();
                return new PlaylistDetailDto(
                    p.Id, p.Name, p.Description, p.CollectionId, p.IsSetlist, p.GigDate,
                    ordered.Count, p.CreatedAt, p.UpdatedAt, ordered);
            })
            .ToList();

        // CollectionDto.playlistCount is live in the collection endpoints — same over the full rows.
        var playlistCountByCollection = allPlaylists
            .Where(p => p.CollectionId != null)
            .GroupBy(p => p.CollectionId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        return new SyncResponse(
            serverTime,
            changedSongs.Select(ToDetailDto).ToList(),
            allSongs.Select(s => s.Id).ToList(),
            playlists,
            allPlaylists.Select(p => p.Id).ToList(),
            collections
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new CollectionDto(
                    c.Id, c.Name, c.Description, c.Category,
                    playlistCountByCollection.TryGetValue(c.Id, out var n) ? n : 0,
                    c.CreatedAt))
                .ToList());
    }

    private static SongDto ToDto(Song s)
        => new(s.Id, s.Title, s.Artist, s.Album, s.Year, s.Key, s.Genre, s.Tempo, s.CapoFret, s.CreatedAt, s.UpdatedAt);

    private static SongDetailDto ToDetailDto(Song s)
        => new(s.Id, s.Title, s.Artist, s.Album, s.Year, s.Key, s.Genre, s.Tempo, s.CapoFret, s.CreatedAt, s.UpdatedAt, s.ChordProText, s.SourceUrl);
}
