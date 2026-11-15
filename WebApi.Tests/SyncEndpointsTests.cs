using WebApi.Dtos;
using WebApi.Endpoints;
using WebApi.Entities;

namespace WebApi.Tests;

/// <summary>
/// MT-04: /api/sync payload assembly. The endpoint queries EF; Build() is the pure part
/// under test — full vs. delta selection, deletion-pruning id lists, playlist song ordering,
/// and live collection playlist counts.
/// </summary>
public class SyncEndpointsTests
{
    private static Song MakeSong(Guid id, string title, DateTime updatedAt, string? text = "[C]test")
        => new()
        {
            Id = id,
            Title = title,
            Artist = "Artist",
            CapoFret = 0,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = updatedAt,
            ChordProText = text!,
        };

    private static Playlist MakePlaylist(Guid id, string name, DateTime updatedAt, Guid? collectionId = null)
        => new()
        {
            Id = id,
            Name = name,
            IsSetlist = false,
            CollectionId = collectionId,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = updatedAt,
        };

    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FullSync_CarriesEverySongAndIdWithChordProText()
    {
        var a = MakeSong(Guid.NewGuid(), "Alpha", T1);
        var b = MakeSong(Guid.NewGuid(), "Bravo", T2);
        var pl = MakePlaylist(Guid.NewGuid(), "Set", T1);
        var links = new[] { new PlaylistSong { PlaylistId = pl.Id, SongId = a.Id, Position = 1 } };

        var payload = SyncEndpoints.Build([a, b], [a, b], [pl], [pl], links, [], T2);

        Assert.Equal(2, payload.Songs.Count);
        Assert.Contains(payload.Songs, s => s.ChordProText == "[C]test");
        Assert.Equal(2, payload.SongIds.Count);
        Assert.Single(payload.Playlists);
        Assert.Equal(pl.Id, payload.PlaylistIds.Single());
    }

    [Fact]
    public void DeltaSync_OnlyChangedSongs_ButFullIdLists()
    {
        var a = MakeSong(Guid.NewGuid(), "Alpha", T0);   // unchanged
        var b = MakeSong(Guid.NewGuid(), "Bravo", T2);   // changed
        var all = new[] { a, b };

        var payload = SyncEndpoints.Build(
            all.Where(s => s.UpdatedAt > T1).ToList(), all,
            [], [], [], [], T2);

        Assert.Equal(b.Id, Assert.Single(payload.Songs).Id);
        // The pruning list covers the whole library, so the client can drop server-side deletions.
        Assert.Contains(a.Id, payload.SongIds);
        Assert.Contains(b.Id, payload.SongIds);
    }

    [Fact]
    public void PlaylistDetail_SongsInPositionOrder()
    {
        var a = MakeSong(Guid.NewGuid(), "Alpha", T1);
        var b = MakeSong(Guid.NewGuid(), "Bravo", T1);
        var pl = MakePlaylist(Guid.NewGuid(), "Set", T1);

        // Links deliberately out of order — the payload must sort by Position.
        var links = new[]
        {
            new PlaylistSong { PlaylistId = pl.Id, SongId = b.Id, Position = 2 },
            new PlaylistSong { PlaylistId = pl.Id, SongId = a.Id, Position = 1 },
        };

        var payload = SyncEndpoints.Build([], [a, b], [pl], [pl], links, [], T2);

        var detail = Assert.Single(payload.Playlists);
        Assert.Equal(new[] { a.Id, b.Id }, detail.Songs.Select(s => s.Id));
        Assert.Equal(2, detail.SongCount);
    }

    [Fact]
    public void CollectionCounts_AreLiveOverTheFullPlaylistList()
    {
        var col = new Collection
        {
            Id = Guid.NewGuid(),
            Name = "Gigs",
            Category = "Other",
            CreatedAt = T0,
        };
        var inCol = MakePlaylist(Guid.NewGuid(), "In", T0, col.Id);
        var inCol2 = MakePlaylist(Guid.NewGuid(), "In2", T0, col.Id);
        var loose = MakePlaylist(Guid.NewGuid(), "Loose", T0);

        var payload = SyncEndpoints.Build([], [], [], [inCol, inCol2, loose], [], [col], T2);

        var dto = Assert.Single(payload.Collections);
        Assert.Equal(2, dto.PlaylistCount);
    }

    [Fact]
    public void ServerTime_PassedThroughVerbatim()
    {
        var payload = SyncEndpoints.Build([], [], [], [], [], [], T2);
        Assert.Equal(T2, payload.ServerTime);
    }
}
