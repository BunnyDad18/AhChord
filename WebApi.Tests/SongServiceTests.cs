using WebApi.Services;

namespace WebApi.Tests;

public class SongServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SongService.HistoryRow Row(Guid songId, string? userId, DateTime viewedAt)
        => new() { SongId = songId, UserId = userId, ViewedAt = viewedAt };

    // --- Random ---------------------------------------------------------------

    [Fact]
    public void PickRandom_Empty_ReturnsNull()
    {
        Assert.Null(SongService.PickRandom(Array.Empty<Guid>()));
    }

    [Fact]
    public void PickRandom_SingleElement_ReturnsIt()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, SongService.PickRandom(new[] { id }));
    }

    [Fact]
    public void PickRandom_AlwaysReturnsAMemberOfTheSet()
    {
        // Property: whatever the playlist/library set is, the pick is always one of its members.
        var ids = new List<Guid>
        {
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        };
        for (var i = 0; i < 500; i++)
        {
            var picked = SongService.PickRandom(ids);
            Assert.NotNull(picked);
            Assert.Contains(picked!.Value, ids);
        }
    }

    // --- Recent: dedupe -------------------------------------------------------

    [Fact]
    public void OrderRecent_DedupesOneEntryPerSong_LatestViewWins()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var rows = new[]
        {
            Row(a, "u1", T0.AddHours(1)), // A viewed at 1h
            Row(b, "u1", T0.AddHours(2)), // B viewed at 2h
            Row(a, "u1", T0.AddHours(3))  // A re-viewed at 3h (latest)
        };

        var ordered = SongService.OrderRecent(rows, "u1", 10);

        // One entry per song; A was re-viewed last so it is most recent.
        Assert.Equal(new[] { a, b }, ordered);
    }

    // --- Recent: most-recent-first -------------------------------------------

    [Fact]
    public void OrderRecent_MostRecentFirst()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var rows = new[]
        {
            Row(a, "u1", T0),
            Row(b, "u1", T0.AddHours(1)),
            Row(c, "u1", T0.AddHours(2))
        };

        Assert.Equal(new[] { c, b, a }, SongService.OrderRecent(rows, "u1", 10));
    }

    // --- Recent: limit --------------------------------------------------------

    [Fact]
    public void OrderRecent_RespectsLimit()
    {
        var rows = Enumerable.Range(0, 10)
            .Select(i => Row(Guid.NewGuid(), "u1", T0.AddHours(i)))
            .ToArray();

        var ordered = SongService.OrderRecent(rows, "u1", 3);

        Assert.Equal(3, ordered.Count);
        // The three most recent (hours 9, 8, 7) survive.
        Assert.Contains(rows[9].SongId, ordered);
        Assert.Contains(rows[8].SongId, ordered);
        Assert.Contains(rows[7].SongId, ordered);
    }

    [Fact]
    public void OrderRecent_NonPositiveLimit_StillReturnsNewest()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var rows = new[]
        {
            Row(a, "u1", T0),
            Row(b, "u1", T0.AddHours(1))
        };

        // limit <= 0 falls back to "at least the newest" rather than an empty list.
        var ordered = SongService.OrderRecent(rows, "u1", 0);
        Assert.Equal(new[] { b }, ordered);
    }

    // --- Recent: per-user isolation -------------------------------------------

    [Fact]
    public void OrderRecent_IgnoresOtherUsersRows()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var rows = new[]
        {
            Row(a, "u1", T0),
            Row(b, "u2", T0.AddHours(1)) // different user — must not appear for u1
        };

        Assert.Equal(new[] { a }, SongService.OrderRecent(rows, "u1", 10));
    }
}
