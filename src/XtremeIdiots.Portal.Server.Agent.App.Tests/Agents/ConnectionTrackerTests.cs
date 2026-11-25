using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public sealed class ConnectionTrackerTests
{
    [Fact]
    public void Correlation_PreservesDistinctObservationIds()
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Complete));
        var join = Join("one");
        var ip = Ip("one");

        var correlatedJoin = Assert.IsType<PlayerConnectedEvent>(tracker.Observe(join));
        var correlatedIp = Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(ip));

        Assert.NotEqual(Guid.Empty, join.ObservationId);
        Assert.NotEqual(join.ObservationId, ip.ObservationId);
        Assert.Equal(join.ObservationId, correlatedJoin.ObservationId);
        Assert.Equal(ip.ObservationId, correlatedIp.ObservationId);
    }

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
        Assert.Null(tracker.GetConnectionId(1, "other"));
        Assert.Equal(reconnect.ConnectionId, tracker.GetConnectionId(1, "ONE"));
        Assert.NotNull(reconnect.ConnectionId);
        Assert.NotEqual(join.ConnectionId, reconnect.ConnectionId);
    }

    [Fact]
    public void MapTransition_PreservesMatchingCarryoverUntilObservedDisconnect()
    {
        var tracker = new ConnectionTracker();
        var first = Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one")));

        tracker.Observe(new MapChangeEvent { Timestamp = DateTime.UtcNow, MapName = "map", GameType = "dm" });
        Assert.Equal(first.ConnectionId, Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
        tracker.Observe(Quit("one"));
        Assert.NotEqual(first.ConnectionId, Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
        var beforeReset = tracker.GetConnectionId(1, "one");
        tracker.Reset();
        Assert.NotEqual(beforeReset, Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
    }

    [Fact]
    public void PartialRosterAndMismatchedIdentity_UseOnlyMatchingConnections()
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Partial));
        Assert.NotNull(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
        tracker.Reset();
        tracker.ObserveRoster(Roster(RconRosterState.Complete));
        var first = Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one")));
        Assert.NotNull(first.ConnectionId);
        Assert.Null(Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("other"))).ConnectionId);
        Assert.Null(Assert.IsType<PlayerDisconnectedEvent>(tracker.Observe(Quit("other"))).ConnectionId);
        Assert.Equal(first.ConnectionId, Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("one"))).ConnectionId);
        Assert.NotEqual(first.ConnectionId, Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("other"))).ConnectionId);
    }

    [Fact]
    public void RosterWithMissingIdentitiesStillTracksValidRows()
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Partial, new ServerRosterPlayer(1, "one", null, null)) with { MissingIdentityCount = 1 });
        Assert.NotNull(Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("one"))).ConnectionId);
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
        Assert.NotNull(Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("other"))).ConnectionId);
        Assert.Null(Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("one"))).ConnectionId);
    }

    [Theory]
    [InlineData(RconRosterState.Partial, true)]
    [InlineData(RconRosterState.Complete, false)]
    public void RosterConflict_ReplacesPriorIdentity(RconRosterState state, bool establishBaseline)
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Complete));
        var first = Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one")));
        Assert.NotNull(first.ConnectionId);

        tracker.ObserveRoster(Roster(state, new ServerRosterPlayer(1, "other", null, null)), establishBaseline);

        Assert.Null(Assert.IsType<PlayerIpResolvedEvent>(tracker.Observe(Ip("one"))).ConnectionId);
        Assert.NotEqual(first.ConnectionId, Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one"))).ConnectionId);
    }

    [Fact]
    public void PartialRosterDoesNotRemoveMissingPlayers()
    {
        var tracker = new ConnectionTracker();
        var joined = Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("one")));
        tracker.ObserveRoster(Roster(RconRosterState.Partial), establishBaseline: false);
        Assert.Equal(joined.ConnectionId, tracker.GetConnectionId(1, "one"));
    }

    [Fact]
    public void DuplicateOrInvalidRosterRowsCannotCreateIdentity()
    {
        var tracker = new ConnectionTracker();
        tracker.ObserveRoster(Roster(RconRosterState.Partial,
            new ServerRosterPlayer(1, "one", null, null),
            new ServerRosterPlayer(1, "other", null, null),
            new ServerRosterPlayer(2, "", null, null)));
        Assert.Null(tracker.GetConnectionId(1, "one"));
        Assert.Null(Assert.IsType<PlayerConnectedEvent>(tracker.Observe(Join("") with { SlotId = -1 })).ConnectionId);
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
