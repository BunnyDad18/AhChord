using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace WebApi.Hubs;

/// <summary>
/// ST-33: resolves the SignalR user id from our JWT so Clients.User(userId) — used by the
/// import progress reporter to push per-user events — targets the right connections.
/// JwtBearer maps inbound claims by default, so "sub" arrives as ClaimTypes.NameIdentifier;
/// check that first and fall back to a literal "sub" (unmapped tokens).
/// Side effect: SessionHub's Context.UserIdentifier now returns the stable user id instead
/// of falling back to ConnectionId (participants are tracked per user across reconnections).
/// </summary>
public sealed class SubClaimUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? connection.User?.FindFirst("sub")?.Value;
}
