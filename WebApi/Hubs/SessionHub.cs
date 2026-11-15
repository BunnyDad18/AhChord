using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;

namespace WebApi.Hubs;

/// <summary>Server->client: current session position (ST-25).</summary>
public record SessionStatePayload(Guid PlaylistId, int SongIndex, int Capo);

/// <summary>Server->client: who is in the session (ST-25).</summary>
public record ParticipantsPayload(Guid PlaylistId, int Count, IReadOnlyList<string> UserIds);

/// <summary>ST-87 server->client: the host's current view (the follow session's
/// state) — broadcast on every publish and on join/leave (the follower count).</summary>
public record FollowStatePayload(
    string HostId,
    string HostName,
    string Kind,
    string? SongId,
    string? SongTitle,
    string? SongArtist,
    string? PlaylistId,
    int? SongIndex,
    long Seq,
    int Followers);

/// <summary>
/// Playlist-scoped real-time sessions (ST-25) + the general follow session
/// (ST-87: Start a session — joined devices follow what the host browses;
/// GET /api/sessions discovers them). State lives in the shared
/// <see cref="SessionRegistry"/> singleton (ST-86) — in-memory: a server restart
/// resets all sessions (acceptable for MVP, documented). Last write wins — no
/// queueing. Next/Prev clamp/wrap within the playlist's song count (captured at
/// join time); SetTranspose validates −12…+12 (the state field is still named Capo
/// on the wire for payload compatibility). [Authorize] = JWT from the handshake
/// (access_token query string / Authorization header); unauthenticated handshakes
/// are rejected with 401.
/// </summary>
[Authorize]
public class SessionHub : Hub
{
    private readonly ApplicationDbContext _db;
    // ST-86: the live sessions moved to a singleton registry so the discovery
    // endpoint (GET /api/sessions) can enumerate them; the hub's behaviour is unchanged.
    private readonly SessionRegistry _registry;

    public SessionHub(ApplicationDbContext db, SessionRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    /// <summary>Join (or create) the playlist's session; returns the current state.
    /// NB: the .NET SignalR invoker does not apply C# default values to omitted
    /// args, so non-C# clients must pass <c>songIndex</c> explicitly (null to join
    /// at the current position).</summary>
    public async Task<SessionStatePayload> Join(Guid playlistId, int? songIndex = null)
    {
        // ST-86: the name rides along — the discovery endpoint presents sessions by name.
        var playlistName = await _db.Playlists.AsNoTracking()
            .Where(p => p.Id == playlistId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync();
        if (playlistName is null)
            throw new HubException("Playlist not found.");

        var count = await _db.PlaylistSongs.CountAsync(ps => ps.PlaylistId == playlistId);

        var state = _registry.Sessions.GetOrAdd(playlistId, _ => new SessionState { PlaylistId = playlistId });
        state.PlaylistName = playlistName; // a rename follows the live session

        var userId = Context.UserIdentifier ?? Context.ConnectionId;

        // Latest-connection-wins per user: a reconnection (proxy restart, network
        // drop) gets a new ConnectionId; drop this user's stale entries first so a
        // dead connection never pins the participant count.
        foreach (var (staleConn, staleUser) in state.Participants)
        {
            if (staleUser == userId)
                state.Participants.TryRemove(staleConn, out _);
        }

        state.Participants[Context.ConnectionId] = userId;
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(playlistId));

        // Keep the song count fresh; clamp the index if the playlist shrank.
        state.SongCount = count;
        if (count == 0 || state.SongIndex >= count)
            state.SongIndex = 0;

        if (songIndex is { } si)
            state.SongIndex = count > 0 ? Math.Clamp(si, 0, count - 1) : 0;

        var payload = new SessionStatePayload(playlistId, state.SongIndex, state.Capo);
        await BroadcastState(playlistId, state);
        await BroadcastParticipants(playlistId, state);
        return payload;
    }

    /// <summary>Leave; the session is discarded when the last participant goes.</summary>
    public async Task Leave(Guid playlistId)
    {
        if (!_registry.Sessions.TryGetValue(playlistId, out var state))
            return;

        // ST-63: also drop the SignalR group — a leaver kept receiving every broadcast
        // of a session it had left, and its eventual disconnect skipped the participant
        // re-broadcast (no entry left), leaving everyone's counts stale.
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(playlistId));

        state.Participants.TryRemove(Context.ConnectionId, out _);
        if (state.Participants.Count == 0)
        {
            _registry.Sessions.TryRemove(playlistId, out _);
            return;
        }
        await BroadcastParticipants(playlistId, state);
    }

    /// <summary>Advance one song (wraps to the first at the end — documented in ST-25).</summary>
    public Task Next(Guid playlistId) => Step(playlistId, +1);

    /// <summary>Go back one song (wraps to the last at the start).</summary>
    public Task Prev(Guid playlistId) => Step(playlistId, -1);

    /// <summary>Set the session transpose (validated −12…+12 semitones) and broadcast it.
    /// ST-55: was SetCapo with a 0–6 cap — the ST-39 client has always sent its −12…+12
    /// transpose through this method, so every negative/+6-exceeding change was rejected
    /// (HubException swallowed by the client) and the session desynced mid-song.</summary>
    public Task SetTranspose(Guid playlistId, int semitones)
    {
        if (semitones < -12 || semitones > 12)
            throw new HubException("Transpose must be between -12 and +12.");
        if (!_registry.Sessions.TryGetValue(playlistId, out var state))
            return Task.CompletedTask;
        state.Capo = semitones;
        return BroadcastState(playlistId, state);
    }

    /// <summary>ST-55: jump the session to a song index (clamped) and broadcast —
    /// the setlist random pick goes through the hub so every participant follows.</summary>
    public Task GoTo(Guid playlistId, int index)
    {
        if (!_registry.Sessions.TryGetValue(playlistId, out var state) || state.SongCount <= 0)
            return Task.CompletedTask;
        state.SongIndex = Math.Clamp(index, 0, state.SongCount - 1);
        return BroadcastState(playlistId, state);
    }

    // ---------- ST-87: the general follow session (join-me browsing) ----------

    /// <summary>Start (or re-register) MY follow session — the general service:
    /// joined devices follow what I browse. Returns the current view (empty at
    /// first; fills as I publish).</summary>
    public async Task<FollowStatePayload> StartFollow()
    {
        var hostId = Context.UserIdentifier ?? Context.ConnectionId;
        var name = Context.User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = Context.User?.FindFirst("name")?.Value ?? "Someone";

        var state = _registry.FollowStates.GetOrAdd(hostId, id => new FollowState { HostId = id });
        state.HostName = name;
        state.HostConnectionId = Context.ConnectionId; // a reconnect takes the session over

        await Groups.AddToGroupAsync(Context.ConnectionId, FollowGroup(hostId));
        return Payload(state);
    }

    /// <summary>The host publishes what they are looking at; every joined device
    /// receives it (seq bumps so clients can order/dedupe). Publishing without
    /// a live session (stopped, reconnecting) is ignored — the client re-starts it.</summary>
    public Task PublishFollow(
        string kind,
        string? songId = null,
        string? songTitle = null,
        string? songArtist = null,
        string? playlistId = null,
        int? songIndex = null)
    {
        var hostId = Context.UserIdentifier ?? Context.ConnectionId;
        if (!_registry.FollowStates.TryGetValue(hostId, out var state)
            || state.HostConnectionId != Context.ConnectionId)
            return Task.CompletedTask;

        state.Kind = kind == "setlist" ? "setlist" : "song";
        state.SongId = songId;
        state.SongTitle = songTitle;
        state.SongArtist = songArtist;
        state.PlaylistId = playlistId;
        state.SongIndex = songIndex;
        state.Seq += 1;
        return BroadcastFollow(state);
    }

    /// <summary>Join a host's follow session; returns their current view (and
    /// tells the host + other followers the count changed).</summary>
    public async Task<FollowStatePayload> JoinFollow(string hostId)
    {
        if (!_registry.FollowStates.TryGetValue(hostId, out var state))
            throw new HubException("That session has ended.");

        state.Followers[Context.ConnectionId] = Context.UserIdentifier ?? Context.ConnectionId;
        await Groups.AddToGroupAsync(Context.ConnectionId, FollowGroup(hostId));

        await BroadcastFollow(state);
        return Payload(state);
    }

    /// <summary>The host ends the session; every follower is told ("followEnded").</summary>
    public async Task EndFollow()
    {
        var hostId = Context.UserIdentifier ?? Context.ConnectionId;
        if (!_registry.FollowStates.TryRemove(hostId, out var state))
            return;
        await Clients.Group(FollowGroup(hostId)).SendAsync("followEnded", hostId);
    }

    /// <summary>A follower leaves the session(s) it joined (the count updates).</summary>
    public async Task LeaveFollow()
    {
        foreach (var (hostId, state) in _registry.FollowStates)
        {
            if (!state.Followers.TryRemove(Context.ConnectionId, out _))
                continue;
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, FollowGroup(hostId));
            await BroadcastFollow(state);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var (playlistId, state) in _registry.Sessions)
        {
            if (!state.Participants.TryRemove(Context.ConnectionId, out _))
                continue;
            if (state.Participants.Count == 0)
            {
                _registry.Sessions.TryRemove(playlistId, out _);
            }
            else
            {
                await BroadcastParticipants(playlistId, state);
            }
        }

        // ST-87: the host disconnecting ENDS the follow session (followers are
        // told); a follower disconnecting just drops from the count.
        foreach (var (hostId, state) in _registry.FollowStates)
        {
            if (state.HostConnectionId == Context.ConnectionId)
            {
                _registry.FollowStates.TryRemove(hostId, out _);
                await Clients.Group(FollowGroup(hostId)).SendAsync("followEnded", hostId);
            }
            else if (state.Followers.TryRemove(Context.ConnectionId, out _))
            {
                await BroadcastFollow(state);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Task Step(Guid playlistId, int delta)
    {
        if (!_registry.Sessions.TryGetValue(playlistId, out var state) || state.SongCount <= 0)
            return Task.CompletedTask;
        state.SongIndex = (state.SongIndex + delta + state.SongCount) % state.SongCount;
        return BroadcastState(playlistId, state);
    }

    private static string GroupName(Guid playlistId) => $"playlist-{playlistId:N}";

    private static string FollowGroup(string hostId) => $"follow-{hostId}";

    private static FollowStatePayload Payload(FollowState s) =>
        new(s.HostId, s.HostName, s.Kind, s.SongId, s.SongTitle, s.SongArtist,
            s.PlaylistId, s.SongIndex, s.Seq, s.Followers.Values.Distinct().Count());

    private Task BroadcastFollow(FollowState state) =>
        Clients.Group(FollowGroup(state.HostId)).SendAsync("followState", Payload(state));

    private Task BroadcastState(Guid playlistId, SessionState state) =>
        Clients.Group(GroupName(playlistId)).SendAsync(
            "sessionState",
            new SessionStatePayload(playlistId, state.SongIndex, state.Capo));

    private Task BroadcastParticipants(Guid playlistId, SessionState state) =>
        Clients.Group(GroupName(playlistId)).SendAsync(
            "participants",
            new ParticipantsPayload(
                playlistId,
                state.Participants.Values.Distinct().Count(),
                state.Participants.Values.Distinct().ToList()));
}
