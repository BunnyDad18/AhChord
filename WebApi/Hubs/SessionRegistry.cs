using System.Collections.Concurrent;

namespace WebApi.Hubs;

/// <summary>Server-side state of one live playlist session (ST-25).</summary>
public sealed class SessionState
{
    public Guid PlaylistId;
    // ST-86: name + start time for session discovery. The name is refreshed on
    // every Join, so a rename follows the live session.
    public string PlaylistName = "";
    public DateTime StartedAt = DateTime.UtcNow;
    public int SongIndex;
    public int Capo;
    public int SongCount;
    // connectionId -> userId. Latest connection per user wins (Join drops that
    // user's stale entries), so a dead connection cannot pin the participant count.
    public readonly ConcurrentDictionary<string, string> Participants = new();
}

/// <summary>
/// ST-87: one user's live FOLLOW session — the general "follow me" service. The
/// host browses (song pages, the player); every publish updates the shared view
/// and every joined device follows it. Keyed by host user id.
/// </summary>
public sealed class FollowState
{
    public string HostId = "";
    public string HostName = "";
    // Latest host connection wins (StartFollow re-registers), so a host
    // reconnect takes the session over instead of orphaning it.
    public string HostConnectionId = "";
    // What the host is looking at: 'song' (any song page/player) or
    // 'setlist' (inside a setlist player — joined devices open that player and
    // the ST-25 transport keeps the songs in sync from there).
    public string Kind = "song";
    public string? SongId;
    public string? SongTitle;
    public string? SongArtist;
    public string? PlaylistId;
    public int? SongIndex;
    // Monotonic publish counter — clients order/dedupe view updates with it.
    public long Seq;
    public DateTime StartedAt = DateTime.UtcNow;
    // connectionId -> follower user id.
    public readonly ConcurrentDictionary<string, string> Followers = new();
}

/// <summary>A live follow session as the ST-87 discovery endpoint presents it.</summary>
public record LiveSession(
    string HostId,
    string HostName,
    string Kind,
    string? SongId,
    string? SongTitle,
    string? SongArtist,
    string? PlaylistId,
    int? SongIndex,
    int Followers,
    DateTime StartedAt);

/// <summary>
/// ST-86/ST-87: live session state shared by the session hub and GET /api/sessions.
/// The ST-25 playlist transport sessions stay in-memory (restart resets — the
/// documented MVP design); the ST-87 follow sessions (the general "join me"
/// service) live in <see cref="FollowStates"/>, keyed by host user id. A follow
/// session exists while its host is connected; when the host disconnects it ends
/// and the followers are told.
/// </summary>
public sealed class SessionRegistry
{
    internal readonly ConcurrentDictionary<Guid, SessionState> Sessions = new();
    internal readonly ConcurrentDictionary<string, FollowState> FollowStates = new();

    /// <summary>Snapshot of the live follow sessions, oldest first.</summary>
    public IReadOnlyList<LiveSession> Snapshot() =>
        FollowStates.Values
            .OrderBy(s => s.StartedAt)
            .Select(s => new LiveSession(
                s.HostId,
                s.HostName,
                s.Kind,
                s.SongId,
                s.SongTitle,
                s.SongArtist,
                s.PlaylistId,
                s.SongIndex,
                s.Followers.Values.Distinct().Count(),
                s.StartedAt))
            .ToList();
}
