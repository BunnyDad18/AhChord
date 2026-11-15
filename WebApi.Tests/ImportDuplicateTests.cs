using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-49: duplicate detection in <see cref="ImportService.CreateSongAsync"/> — same title + artist
/// (case-insensitive) with identical normalized content is a no-op that returns the existing song.
/// In-memory DB, following the ChordProConversionJobTests fixture pattern.
/// </summary>
public class ImportDuplicateTests
{
    /// <summary>Canonical chart: metadata header + one chorded line.</summary>
    private const string Chart = """
        {title: Africa}
        {artist: Toto}
        {chorus}
        [E] La la la [A] la la
        """;

    /// <summary>ST-48 variant notation of the same chart — normalizes to exactly <see cref="Chart"/>.</summary>
    private const string ChartVariant = """
        {t:Africa}
        {a:Toto}
        Chorus:
        [E] La la la [A] la la
        """;

    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task SameTitleArtistContent_ReturnsExisting_NoSecondRow()
    {
        var db = NewDb();
        var (first, firstDup) = await ImportService.CreateSongAsync(db, Chart, null, null);
        Assert.False(firstDup);

        var (second, secondDup) = await ImportService.CreateSongAsync(db, Chart, null, null);
        Assert.True(secondDup);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(await db.Songs.ToListAsync());
    }

    [Fact]
    public async Task SameTitleArtist_DifferentContent_CreatesNew()
    {
        const string other = """
            {title: Africa}
            {artist: Toto}
            {chorus}
            [C] Different [G] chords here
            """;
        var db = NewDb();
        await ImportService.CreateSongAsync(db, Chart, null, null);
        var (second, dup) = await ImportService.CreateSongAsync(db, other, null, null);

        Assert.False(dup);
        Assert.Equal(2, await db.Songs.CountAsync());
    }

    [Fact]
    public async Task SameTitle_DifferentArtist_CreatesNew()
    {
        var db = NewDb();
        var (first, _) = await ImportService.CreateSongAsync(db, Chart, null, null);
        // Body override beats the header — resolved artist differs, so this is a different song.
        var (second, dup) = await ImportService.CreateSongAsync(db, Chart, null, "Someone Else");

        Assert.False(dup);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await db.Songs.CountAsync());
    }

    [Fact]
    public async Task CaseInsensitive_TitleAndArtist_MatchAsDuplicate()
    {
        var db = NewDb();
        var (first, _) = await ImportService.CreateSongAsync(db, Chart, null, null);
        var (second, dup) = await ImportService.CreateSongAsync(db, Chart, "africa", "toto");

        Assert.True(dup);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task OneSideMissingArtist_NotADuplicate()
    {
        const string noArtist = """
            {title: Africa}
            {chorus}
            [E] La la la [A] la la
            """;
        var db = NewDb();
        await ImportService.CreateSongAsync(db, noArtist, null, null);
        // Can't confirm it's the same song without an artist on both sides.
        var (_, dup) = await ImportService.CreateSongAsync(db, Chart, null, null);

        Assert.False(dup);
        Assert.Equal(2, await db.Songs.CountAsync());
    }

    [Fact]
    public async Task VariantNotation_NormalizesToSameContent_Duplicate()
    {
        // ST-48 synergy: shorthand headers + bare headings normalize to the canonical chart.
        var db = NewDb();
        var (first, _) = await ImportService.CreateSongAsync(db, Chart, null, null);
        var (second, dup) = await ImportService.CreateSongAsync(db, ChartVariant, null, null);

        Assert.True(dup);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Duplicate_WithPlaylist_AddsExistingSongOnce()
    {
        var db = NewDb();
        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = "Setlist",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();

        var (first, _) = await ImportService.CreateSongAsync(db, Chart, null, null);
        Assert.False(await db.PlaylistSongs.AnyAsync(ps => ps.SongId == first.Id));

        // Re-importing into a setlist adds the existing song — idempotent setlist building.
        var (second, dup) = await ImportService.CreateSongAsync(db, Chart, null, null, playlistId: playlist.Id);
        Assert.True(dup);
        Assert.Equal(first.Id, second.Id);
        var membership = await db.PlaylistSongs.SingleAsync(ps => ps.SongId == first.Id);
        Assert.Equal(1, membership.Position);

        // A third import must not add a second membership row.
        await ImportService.CreateSongAsync(db, Chart, null, null, playlistId: playlist.Id);
        Assert.Single(await db.PlaylistSongs.Where(ps => ps.SongId == first.Id).ToListAsync());
    }
}
