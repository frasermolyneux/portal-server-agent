using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public sealed class ConnectionTrackerTests
{
    [Fact]
    public void CompleteBaseline_CorrelatesJoinLateIpAndQuit_AndReconnectGetsNewId()
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Complete));

        var join = Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one") with { IpAddress = "9.9.9.9" }));
        var ip = Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("one")));
        var quit = Assert.IsType<PlayerDisconnectedEvent>(tracker.Observe(Quit("one")));
        var reconnect = Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one")));

        Assert.NotNull(join.ConnectionId);
        Assert.Empty(join.IpAddress);
        Assert.Equal(join.ConnectionId, ip.ConnectionId);
        Assert.Equal(join.ConnectionId, quit.ConnectionId);
        Assert.NotNull(reconnect.ConnectionId);
        Assert.NotEqual(join.ConnectionId, reconnect.ConnectionId);
    }

    [Fact]
    public void UnknownOccupantAndTransition_DoNotInventContinuity()
    {
        var tracker = new ConnectionTracker();
        Assert.Null(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
        tracker.ObserveRoster(Roster(RconRosterState.Complete, new ServerRosterPlayer(1, "one", null, null)));
        Assert.Null(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);

        tracker.Observe(new MapChangeEvent { Timestamp = DateTime.UtcNow, MapName = "map", GameType = "dm" });
        Assert.Null(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
        tracker.Reset();
        Assert.Null(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
    }

    [Fact]
    public void IncompleteRosterAndMismatchedIdentity_DoNotClaimConnection()
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Partial));
        Assert.Null(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
        tracker.Reset();
        tracker.ObserveRoster(Roster(RconRosterState.Complete));
        var first = Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one")));
        Assert.NotNull(first.ConnectionId);
        Assert.Null(Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("other"))).ConnectionId);
        Assert.Null(Assert.IsType<PlayerDisconnectedEvent>(tracker.Observe(Quit("other"))).ConnectionId);
        Assert.Equal(first.ConnectionId, Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("one"))).ConnectionId);
        Assert.Null(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("other"))).ConnectionId);
    }

    [Fact]
    public void RosterWithMissingIdentitiesCannotEstablishEmptySlots()
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Complete) with { MissingIdentityCount = 1 });
        Assert.Null(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
    }

    [Fact]
    public void CompleteRosterRetainsOnlyMatchingObservedConnection()
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Complete));
        var join = Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one")));
        tracker.ObserveRoster(Roster(RconRosterState.Complete, new ServerRosterPlayer(1, "ONE", null, null)));
        Assert.Equal(join.ConnectionId, Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("one"))).ConnectionId);

        tracker.ObserveRoster(Roster(RconRosterState.Complete, new ServerRosterPlayer(1, "other", null, null)));
        Assert.Null(Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("other"))).ConnectionId);
        Assert.Null(Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("one"))).ConnectionId);
    }

    [Fact]
    public void MapTransition_DoesNotTreatCarryoverAsNewWhenBaselineWasEmpty()
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Complete));
        Assert.NotNull(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);

        tracker.Reset(preservePossibleCarryovers: true);
        tracker.ObserveRoster(Roster(RconRosterState.Complete));
        Assert.Null(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
        tracker.Observe(Quit("one"));
        Assert.NotNull(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
    }

    private static ServerRosterSnapshot Roster(RconRosterState state, params ServerRosterPlayer[] players)
        => new(DateTime.UtcNow, state, players, 0, 0, 0);

    private static PlayerConnectedEvent Join(string guid)
        => new() { Timestamp = DateTime.UtcNow, PlayerGuid = guid, Username = "player", SlotId = 1 };

    private static PlayerDisconnectedEvent Quit(string guid)
        => new() { Timestamp = DateTime.UtcNow, PlayerGuid = guid, Username = "player", SlotId = 1 };

    private static PlayerIpResolvedEvent Ip(string guid)
        => new() { Timestamp = DateTime.UtcNow, PlayerGuid = guid, IpAddress = "1.2.3.4", SlotId = 1 };
}
