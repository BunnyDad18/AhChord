using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;

namespace WebApi.Services;

/// <summary>
/// MT-05: device → server restore push (POST /api/sync/push). The MT-04 sync is a one-way
/// server → device pull whose pruning deletes cached rows the server does not know — so a
/// rebuilt/empty server would destroy the device's only copy of the library. The push closes
/// that gap: the client uploads the cache rows the server has never seen BEFORE it prunes,
/// and the endpoint hands back the clientId → serverId mapping so the cache re-keys cleanly.
///
/// Songs go through <see cref="ImportService.CreateSongAsync"/> — the single chokepoint that
/// creates a song from ChordPro (bounded text, normalization, parser validation, title+artist+
/// text dedup) — so a retry after a failed push is a no-op merge, never a duplicate. A broken
/// chart is a per-row failure in the response, never a 400 that fails the whole batch.
/// Playlists dedup by name (case-insensitive): an existing playlist only gains the missing
/// songs (retry-idempotent); a new name is created with its songs in position order.
/// </summary>
public static class SyncPushService
{
    public const int MaxSongsPerRequest = 500;
    public const int MaxPlaylistsPerRequest = 100;

    public static async Task<PushLibraryResponse> PushAsync(ApplicationDbContext db, PushLibraryRequest request)
    {
        var songResults = new List<PushSongResult>();
        var serverIdByClientId = new Dictionary<Guid, Guid>();

        foreach (var song in request.Songs ?? [])
        {
            if (!serverIdByClientId.ContainsKey(song.ClientId))
                serverIdByClientId[song.ClientId] = Guid.Empty; // first occurrence wins; later ones skipped
            else
                continue;

            try
            {
                var (created, duplicate) = await ImportService.CreateSongAsync(
                    db, song.ChordProText, song.Title, song.Artist,
                    genre: null, year: song.Year, album: song.Album,
                    playlistId: null, sourceUrl: song.SourceUrl);

                // Restore keeps the device metadata the import path has no body field for —
                // only on a fresh create, and never over a value the {key:}/{capo:} headers resolved.
                var patched = false;
                if (!duplicate)
                {
                    if (created.Key is null && !string.IsNullOrWhiteSpace(song.Key))
                    {
                        created.Key = ImportService.Clean(song.Key);
                        patched = true;
                    }
                    if (created.Tempo is null && song.Tempo is > 0)
                    {
                        created.Tempo = song.Tempo;
                        patched = true;
                    }
                    if (created.CapoFret == 0 && song.CapoFret is >= 1 and <= 6)
                    {
                        created.CapoFret = song.CapoFret.Value;
                        patched = true;
                    }
                    if (patched)
                        await db.SaveChangesAsync();
                }

                serverIdByClientId[song.ClientId] = created.Id;
                songResults.Add(new(song.ClientId, created.Id, !duplicate, null));
            }
            catch (ImportException ex)
            {
                songResults.Add(new(song.ClientId, Guid.Empty, false, ex.Code));
            }
        }

        var playlistResults = new List<PushPlaylistResult>();
        var now = DateTime.UtcNow;

        foreach (var pushPlaylist in request.Playlists ?? [])
        {
            var name = ImportService.Clean(pushPlaylist.Name);
            if (name is null || name.Length > InputLimits.MaxMetaFieldLength)
            {
                playlistResults.Add(new(pushPlaylist.ClientId, Guid.Empty, false, "validation-failed"));
                continue;
            }

            // Position order over the ids the song phase mapped; unknown/failed ids drop out.
            var songIds = pushPlaylist.SongClientIds
                .Select(clientId => serverIdByClientId.TryGetValue(clientId, out var serverId) ? serverId : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            var lowerName = name.ToLower();
            var existing = await db.Playlists.FirstOrDefaultAsync(p => p.Name.ToLower() == lowerName);
            if (existing is not null)
            {
                // Name match = the same setlist — a retry re-pushes it; append only the missing songs.
                var linked = await db.PlaylistSongs
                    .Where(ps => ps.PlaylistId == existing.Id)
                    .Select(ps => ps.SongId)
                    .ToListAsync();
                var set = new HashSet<Guid>(linked);
                var position = linked.Count;
                foreach (var songId in songIds)
                {
                    if (set.Add(songId))
                        db.PlaylistSongs.Add(new PlaylistSong { PlaylistId = existing.Id, SongId = songId, Position = ++position });
                }
                existing.UpdatedAt = now; // membership edit bumps the sync stamp (MT-04 rule)
                playlistResults.Add(new(pushPlaylist.ClientId, existing.Id, false, null));
                continue;
            }

            var playlist = new Playlist
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = ImportService.Clean(pushPlaylist.Description),
                IsSetlist = pushPlaylist.IsSetlist,
                // The client sends UTC ISO stamps; pin the Kind so Npgsql's timestamptz accepts it.
                GigDate = pushPlaylist.GigDate is { } gig ? DateTime.SpecifyKind(gig, DateTimeKind.Utc) : null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Playlists.Add(playlist);
            for (var i = 0; i < songIds.Count; i++)
                db.PlaylistSongs.Add(new PlaylistSong { PlaylistId = playlist.Id, SongId = songIds[i], Position = i + 1 });
            playlistResults.Add(new(pushPlaylist.ClientId, playlist.Id, true, null));
        }

        await db.SaveChangesAsync();
        return new(DateTime.UtcNow, songResults, playlistResults);
    }
}
