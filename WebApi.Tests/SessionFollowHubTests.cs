using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using WebApi.Hubs;

namespace WebApi.Tests;

/// <summary>
/// ST-87: the general follow session on the hub — Start (host registers, name from
/// the "name" claim), Publish (view broadcast, seq bumps, only the host's connection
/// may publish), Join (follower counted, payload returned, unknown host rejected),
/// End/Leave (followers told / count updated), and disconnect cleanup (host leaves →
/// session ENDS + followEnded; follower leaves → count drops). Hand-rolled hub fakes
/// (the repo carries no mocking library).
/// </summary>
public class SessionFollowHubTests
{
    // ---------- fakes ----------

    private sealed class FakeContext : HubCallerContext
    {
        public FakeContext(string connectionId, string userId, string name)
        {
            ConnectionId = connectionId;
            UserIdentifier = userId;
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, name) }, authenticationType: "test"));
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier { get; }
        public override ClaimsPrincipal User { get; }
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort() { }
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } =
            new Microsoft.AspNetCore.Http.Features.FeatureCollection();
    }

    private sealed class FakeClient : ISingleClientProxy
    {
        public List<(string Method, object?[]? Args)> Sent { get; } = [];
        public string? Sender => null;
        public Task SendCoreAsync(string method, object?[]? args, CancellationToken ct = default)
        {
            Sent.Add((method, args));
            return Task.CompletedTask;
        }
        public Task<T> InvokeCoreAsync<T>(string method, object?[]? args, CancellationToken ct = default)
            => Task.FromResult(default(T)!);
    }

    private sealed class FakeClients : IHubCallerClients
    {
        public FakeClient AllProxy { get; } = new();
        public System.Collections.Concurrent.ConcurrentDictionary<string, FakeClient> GroupProxies { get; } = [];
        public FakeClient CallerProxy { get; } = new();

        public IClientProxy Caller => CallerProxy;
        public IClientProxy All => AllProxy;
        public IClientProxy Others => AllProxy;
        public IClientProxy OthersInGroup(string groupName) => Group(groupName);
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => AllProxy;
        public IClientProxy Client(string connectionId) => AllProxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => AllProxy;
        public IClientProxy Group(string groupName) => GroupProxies.GetOrAdd(groupName, _ => new FakeClient());
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => AllProxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Group(groupName);
        public IClientProxy User(string userId) => AllProxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => AllProxy;
    }

    private sealed class FakeGroups : IGroupManager
    {
        public Dictionary<string, HashSet<string>> Members { get; } = [];
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        {
            (Members.TryGetValue(groupName, out var m) ? m : Members[groupName] = []).Add(connectionId);
            return Task.CompletedTask;
        }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        {
            if (Members.TryGetValue(groupName, out var m)) m.Remove(connectionId);
            return Task.CompletedTask;
        }
    }

    private static (SessionHub Hub, FakeClients Clients, FakeGroups Groups) NewHub(
        SessionRegistry registry, string connectionId, string userId, string name)
    {
        var clients = new FakeClients();
        var hub = new SessionHub(null!, registry)
        {
            Context = new FakeContext(connectionId, userId, name),
            Clients = clients,
            Groups = new FakeGroups(),
        };
        return (hub, clients, (FakeGroups)hub.Groups);
    }

    private static FollowStatePayload PayloadOf(FakeClient groupClient, int from = 0)
        => groupClient.Sent.Skip(from).First(a => a.Method == "followState").Args![0]
           as FollowStatePayload ?? throw new InvalidOperationException("no followState broadcast");

    // ---------- tests ----------

    [Fact]
    public async Task Start_Register_Publish_Join_End_FullFlow()
    {
        var registry = new SessionRegistry();
        var (host, hostClients, hostGroups) = NewHub(registry, "c-host", "u-1", "Ada");

        // Start: session registers under the host's user id, group membership set.
        var started = await host.StartFollow();
        Assert.Equal("u-1", started.HostId);
        Assert.Equal("Ada", started.HostName);
        Assert.True(registry.FollowStates.ContainsKey("u-1"));
        Assert.Contains("c-host", hostGroups.Members["follow-u-1"]);

        // Publish: the host's view broadcasts with a bumping seq.
        await host.PublishFollow("song", "s-1", "Amazing Grace", "John Newton");
        var group = hostClients.GroupProxies["follow-u-1"];
        var state = PayloadOf(group);
        Assert.Equal(1, state.Seq);
        Assert.Equal("Amazing Grace", state.SongTitle);
        Assert.Equal(0, state.Followers);

        // Join: the follower gets the current view; the join broadcast (count=1)
        // fans out through the joining hub instance's group proxy.
        var (fan, fanClients, _) = NewHub(registry, "c-fan", "u-2", "Ben");
        var joined = await fan.JoinFollow("u-1");
        Assert.Equal("Amazing Grace", joined.SongTitle);
        Assert.Equal(1, joined.Followers);
        Assert.Equal(1, PayloadOf(fanClients.GroupProxies["follow-u-1"]).Followers);

        // End: the followers are told, the session is gone.
        await host.EndFollow();
        Assert.False(registry.FollowStates.ContainsKey("u-1"));
        Assert.Contains(group.Sent, s => s.Method == "followEnded");

        // Joining an ended session is rejected.
        await Assert.ThrowsAsync<HubException>(() => fan.JoinFollow("u-1"));
    }

    [Fact]
    public async Task Publish_OnlyFromTheHostConnection()
    {
        var registry = new SessionRegistry();
        var (host, hostClients, _) = NewHub(registry, "c-host", "u-1", "Ada");
        await host.StartFollow();
        await host.PublishFollow("song", "s-1", "Real Song", null);

        // A follower's connection may not publish the host's view.
        var (fan, _, _) = NewHub(registry, "c-fan", "u-2", "Ben");
        await fan.JoinFollow("u-1");
        await fan.PublishFollow("song", "s-hijack", "Stolen", null);

        Assert.Equal("s-1", registry.FollowStates["u-1"].SongId);
        Assert.DoesNotContain("Stolen", registry.FollowStates["u-1"].SongTitle);
    }

    [Fact]
    public async Task HostDisconnect_EndsSession_FollowerDisconnect_DropsCount()
    {
        var registry = new SessionRegistry();
        var (host, hostClients, _) = NewHub(registry, "c-host", "u-1", "Ada");
        await host.StartFollow();
        var (fan, _, _) = NewHub(registry, "c-fan", "u-2", "Ben");
        await fan.JoinFollow("u-1");

        // Follower disconnects: count drops, session survives.
        await fan.OnDisconnectedAsync(null);
        Assert.True(registry.FollowStates.ContainsKey("u-1"));
        Assert.Empty(registry.FollowStates["u-1"].Followers);

        // Host disconnects: the session ENDS and the group is told.
        await host.OnDisconnectedAsync(null);
        Assert.False(registry.FollowStates.ContainsKey("u-1"));
        Assert.Contains(hostClients.GroupProxies["follow-u-1"].Sent, s => s.Method == "followEnded");
    }

    [Fact]
    public async Task LeaveFollow_DropsFromTheCount()
    {
        var registry = new SessionRegistry();
        var (host, _, _) = NewHub(registry, "c-host", "u-1", "Ada");
        await host.StartFollow();
        var (fan, fanClients, _) = NewHub(registry, "c-fan", "u-2", "Ben");
        await fan.JoinFollow("u-1");

        await fan.LeaveFollow();
        Assert.Empty(registry.FollowStates["u-1"].Followers);
        // fan's proxy: join broadcast (Followers 1), then the leave count (0).
        Assert.Equal(0, PayloadOf(fanClients.GroupProxies["follow-u-1"], from: 1).Followers);
    }
}
