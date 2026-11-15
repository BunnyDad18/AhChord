using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WebApi.Hubs;

namespace WebApi.Endpoints;

/// <summary>
/// ST-86/ST-87: session discovery for the Start/Join UI. GET /api/sessions answers
/// the "is anyone hosting a session right now?" check: AhChord is ONE server on the
/// network reached on the same origin/port by every device, so the sessions live on
/// this server ARE the sessions on the network — this enumerates the live follow
/// sessions (host, what they're viewing, follower count). Like every /api endpoint
/// it requires a JWT (names and titles are private data).
/// </summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/sessions", (SessionRegistry registry) => Results.Ok(registry.Snapshot()));
    }
}
