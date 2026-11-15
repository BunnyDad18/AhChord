using WebApi.Endpoints;

namespace WebApi.Tests;

/// <summary>
/// Bug report #99: the playlist gig-date parsing rules (the PUT/POST endpoints
/// route every gigDate value through TryGigDate).
/// </summary>
public class PlaylistGigDateTests
{
    [Fact]
    public void Absent_LeavesUnchanged()
    {
        Assert.True(PlaylistEndpoints.TryGigDate(null, out var date));
        Assert.Null(date);
    }

    [Fact]
    public void Empty_Clears()
    {
        Assert.True(PlaylistEndpoints.TryGigDate("", out var date));
        Assert.Null(date);
        Assert.True(PlaylistEndpoints.TryGigDate("   ", out date));
        Assert.Null(date);
    }

    [Fact]
    public void IsoDate_ParsesToUtcMidnight()
    {
        Assert.True(PlaylistEndpoints.TryGigDate("2026-11-15", out var date));
        Assert.Equal(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), date);
        Assert.Equal(DateTimeKind.Utc, date!.Value.Kind);
    }

    [Theory]
    [InlineData("15/11/2026")]
    [InlineData("2026-11-15T10:30")]
    [InlineData("nov 15")]
    [InlineData("2026-13-01")]
    public void Invalid_IsRejected(string value)
    {
        Assert.False(PlaylistEndpoints.TryGigDate(value, out var date));
        Assert.Null(date);
    }
}
