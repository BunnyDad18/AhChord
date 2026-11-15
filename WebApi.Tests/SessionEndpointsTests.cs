using Microsoft.Extensions.DependencyInjection;
using WebApi.Hubs;

namespace WebApi.Tests;

/// <summary>
/// ST-87: GET /api/sessions — discovery for the Start/Join UI, now enumerating the
/// GENERAL follow sessions (host, current view, follower count). The endpoint reads
/// the hub's SessionRegistry singleton; tests seed it through the fixture's service
/// provider, the same instance the endpoint resolves. Runs in the shared ST-82
/// collection app; usernames are unique per test.
/// </summary>
[Collection("ST-82 endpoint app")]
public class SessionEndpointsTests
{
    private readonly ApiTestFactory _app;

    public SessionEndpointsTests(ApiTestFactory app) => _app = app;

    private SessionRegistry Registry => _app.Services.GetRequiredService<SessionRegistry>();

    [Fact]
    public async Task Sessions_Anonymous_401()
    {
        var response = await _app.CreateClient().GetAsync("/api/sessions");
        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task Sessions_ListsFollowSessions_CountsDistinctFollowers()
    {
        var user = await _app.RegisterUserAsync();
        var hostId = Guid.NewGuid().ToString();

        var state = new FollowState
        {
            HostId = hostId,
            HostName = "Ada",
            Kind = "song",
            SongId = "song-1",
            SongTitle = "Amazing Grace",
            SongArtist = "John Newton",
            StartedAt = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
        };
        state.Followers["conn-1"] = "fan-1";
        state.Followers["conn-2"] = "fan-1"; // same user, second (stale) connection
        state.Followers["conn-3"] = "fan-2";
        Registry.FollowStates[hostId] = state;

        try
        {
            var response = await _app.AuthedClient(user.Token).GetAsync("/api/sessions");
            Assert.Equal(200, (int)response.StatusCode);
            var list = await ApiTestFactory.ReadJsonAsync<List<LiveSession>>(response);

            var mine = Assert.Single(list, s => s.HostId == hostId);
            Assert.Equal("Ada", mine.HostName);
            Assert.Equal("Amazing Grace", mine.SongTitle);
            Assert.Equal("John Newton", mine.SongArtist);
            Assert.Equal("song", mine.Kind);
            Assert.Equal(2, mine.Followers); // conn-1+conn-2 collapse to fan-1
        }
        finally
        {
            Registry.FollowStates.TryRemove(hostId, out _);
        }
    }
}
