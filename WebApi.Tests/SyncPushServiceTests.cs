using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// MT-05: the restore-push service — a device uploads the cache rows the server has never
/// seen before its client prunes. In-memory DB, following the ImportDuplicateTests fixture
/// pattern: songs ride the ImportService dedup (retry-safe), playlists dedup by name, and a
/// broken row is a per-row failure, never a failed batch.
/// </summary>
public class SyncPushServiceTests
{
    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PushSong Song(Guid clientId, string title, string text = "[C] chorded [Am] lines")
        => new(clientId, title, "Artist", null, null, null, null, null, null, text, null);

    [Fact]
    public async Task Push_CreatesSongs_AndReturnsClientIdMapping()
    {
        var db = NewDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var response = await SyncPushService.PushAsync(
            db, new PushLibraryRequest([Song(a, "Alpha"), Song(b, "Bravo")], null));

        Assert.All(response.Songs, r => Assert.True(r.Created));
        Assert.Null(response.Songs.Single(r => r.ClientId == a).Error);
        var songs = await db.Songs.ToListAsync();
        Assert.Equal(2, songs.Count);
        Assert.Equal(songs.Single(s => s.Title == "Alpha").Id, response.Songs.Single(r => r.ClientId == a).Id);
    }

    [Fact]
    public async Task Push_RetryMerges_NoDuplicates()
    {
        var db = NewDb();
        var a = Guid.NewGuid();
        var request = new PushLibraryRequest([Song(a, "Alpha")], null);

        var first = await SyncPushService.PushAsync(db, request);
        var second = await SyncPushService.PushAsync(db, request);

        Assert.True(Assert.Single(first.Songs).Created);
        var merged = Assert.Single(second.Songs);
        Assert.False(merged.Created);
        Assert.Equal(Assert.Single(first.Songs).Id, merged.Id);
        Assert.Single(await db.Songs.ToListAsync());
    }

    [Fact]
    public async Task Push_BrokenChartFailsPerRow_TheRestLands()
    {
        var db = NewDb();
        var good = Guid.NewGuid();
        var noText = Guid.NewGuid();
        var notChordPro = Guid.NewGuid();

        var response = await SyncPushService.PushAsync(db, new PushLibraryRequest(
        [
            Song(good, "Good"),
            Song(noText, "Empty", "   "),
            Song(notChordPro, "Prose", "just some words with no chords at all"),
        ], null));

        Assert.True(response.Songs.Single(r => r.ClientId == good).Created);
        Assert.Equal("validation-failed", response.Songs.Single(r => r.ClientId == noText).Error);
        Assert.Equal("not-chordpro", response.Songs.Single(r => r.ClientId == notChordPro).Error);
        Assert.Single(await db.Songs.ToListAsync());
    }

    [Fact]
    public async Task Push_KeepsDeviceMetadata_WhenTheChartCarriesNone()
    {
        var db = NewDb();
        var id = Guid.NewGuid();
        var pushed = new PushSong(id, "Alpha", "Artist", "Album", 1979, "G", "Rock", 120, 2,
            "[C] chorded [Am] lines", null);

        var response = await SyncPushService.PushAsync(db, new PushLibraryRequest([pushed], null));

        var created = await db.Songs.SingleAsync(s => s.Id == response.Songs.Single().Id);
        Assert.Equal("G", created.Key);
        Assert.Equal(120, created.Tempo);
        Assert.Equal(2, created.CapoFret);
    }

    [Fact]
    public async Task Push_HeaderDirectivesBeatDeviceMetadata()
    {
        var db = NewDb();
        var id = Guid.NewGuid();
        const string chart = """
            {title: Alpha}
            {key: A}
            {capo: 3}
            [C] chorded [Am] lines
            """;
        var pushed = new PushSong(id, "Alpha", null, null, null, "G", null, null, 1, chart, null);

        var response = await SyncPushService.PushAsync(db, new PushLibraryRequest([pushed], null));

        var created = await db.Songs.SingleAsync(s => s.Id == response.Songs.Single().Id);
        Assert.Equal("A", created.Key);
        Assert.Equal(3, created.CapoFret);
    }

    [Fact]
    public async Task Push_PlaylistLandsWithRemappedSongsInOrder()
    {
        var db = NewDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var pl = Guid.NewGuid();

        var response = await SyncPushService.PushAsync(db, new PushLibraryRequest(
            [Song(a, "Alpha"), Song(b, "Bravo")],
            [new PushPlaylist(pl, "Friday set", "gig", true, new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), [b, a])]));

        var playlistId = response.Playlists.Single().Id;
        var created = await db.Playlists.SingleAsync(p => p.Id == playlistId);
        Assert.Equal("Friday set", created.Name);
        Assert.True(created.IsSetlist);
        Assert.Equal(new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), created.GigDate);

        var links = await db.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId).OrderBy(ps => ps.Position).ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(response.Songs.Single(r => r.ClientId == b).Id, links[0].SongId);
        Assert.Equal(response.Songs.Single(r => r.ClientId == a).Id, links[1].SongId);
    }

    [Fact]
    public async Task Push_PlaylistRetry_MergesByName_NoDuplicateLinks()
    {
        var db = NewDb();
        var a = Guid.NewGuid();
        var pl = Guid.NewGuid();
        var request = new PushLibraryRequest(
            [Song(a, "Alpha")],
            [new PushPlaylist(pl, "Friday set", null, false, null, [a])]);

        var first = await SyncPushService.PushAsync(db, request);
        var second = await SyncPushService.PushAsync(db, request);

        Assert.True(Assert.Single(first.Playlists).Created);
        var merged = Assert.Single(second.Playlists);
        Assert.False(merged.Created);
        Assert.Equal(Assert.Single(first.Playlists).Id, merged.Id);

        Assert.Single(await db.Playlists.ToListAsync());
        Assert.Single(await db.PlaylistSongs.ToListAsync());
    }

    [Fact]
    public async Task Push_PlaylistSkipsSongsThatFailedToLand()
    {
        var db = NewDb();
        var good = Guid.NewGuid();
        var broken = Guid.NewGuid();
        var pl = Guid.NewGuid();

        var response = await SyncPushService.PushAsync(db, new PushLibraryRequest(
            [Song(good, "Good"), Song(broken, "Empty", "   ")],
            [new PushPlaylist(pl, "Set", null, false, null, [good, broken])]));

        var playlistId = response.Playlists.Single().Id;
        var links = await db.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId).ToListAsync();
        Assert.Single(links);
    }

    [Fact]
    public async Task Push_BlankPlaylistNameFailsPerRow()
    {
        var db = NewDb();
        var response = await SyncPushService.PushAsync(db, new PushLibraryRequest(
            null, [new PushPlaylist(Guid.NewGuid(), "   ", null, false, null, [])]));

        Assert.Equal("validation-failed", response.Playlists.Single().Error);
        Assert.Empty(await db.Playlists.ToListAsync());
    }
}
