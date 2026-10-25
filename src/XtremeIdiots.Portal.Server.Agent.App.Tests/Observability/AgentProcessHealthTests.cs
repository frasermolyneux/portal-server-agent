using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.Observability;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Observability;

public class AgentProcessHealthTests
{
    [Fact]
    public void MissingStaleAndEmptyInventoryCannotProduceHealthyFleet()
    {
        var now = DateTimeOffset.UtcNow;
        var health = new AgentProcessHealth();
        Assert.Equal("unknown", health.Evaluate(null, false, now).Status);
        Assert.Equal("unknown", health.Evaluate(new(now, []), false, now).Status);
        Assert.Equal("unknown", health.Evaluate(new(now.AddMinutes(-16), [new(Guid.NewGuid(), true, true)]), false, now).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndividualAndWholeFleetMissingProgressRemainDegradedAfterStartupGrace(bool bans)
    {
        var now = DateTimeOffset.UtcNow;
        var healthy = Guid.NewGuid();
        var absent = Guid.NewGuid();
        var inventory = new AgentInventory(now, [new(healthy, true, true), new(absent, true, true)]);
        var health = new AgentProcessHealth();
        Assert.Equal("unknown", health.Evaluate(inventory, bans, now).Status);
        var assessed = now.AddMinutes(bans ? 16 : 6);
        health.Heartbeats[healthy] = new(Guid.Empty, assessed);
        var liveRuns = new Dictionary<Guid, Guid> { [healthy] = Guid.Empty };
        health.BanChecks[healthy] = new(assessed, true, null, null, null);
        var result = health.Evaluate(inventory with { ObservedAt = assessed }, bans, assessed, liveRuns);
        Assert.Equal(("degraded", 2, 1, 1, 0), result);
        health.Heartbeats.Clear();
        health.BanChecks.Clear();
        Assert.Equal(("degraded", 2, 0, 2, 0),
            health.Evaluate(inventory with { ObservedAt = assessed }, bans, assessed, liveRuns));
    }

    [Fact]
    public void BanDeliveryAndImportMustConvergeRatherThanTreatingPublicationAsPersistence()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var inventory = new AgentInventory(now, [new(id, true, true)]);
        var health = new AgentProcessHealth();
        health.BanChecks[id] = new(now, true, "new", now.AddMinutes(-11), null);
        Assert.Equal("degraded", health.Evaluate(inventory, true, now).Status);
        health.BanChecks[id] = new(now, true, null, null, now.AddMinutes(-21));
        Assert.Equal("degraded", health.Evaluate(inventory, true, now).Status);
        health.BanChecks[id] = new(now, true, null, null, null);
        Assert.Equal("operational", health.Evaluate(inventory, true, now).Status);
        Assert.Equal("degraded", health.Evaluate(new(now, [new(id, false, true)]), true, now).Status);
    }

    [Fact]
    public void DisablingAndReenablingIntentDoesNotReuseOldHealthyEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var health = new AgentProcessHealth();
        var inventory = new AgentInventory(now, [new(id, true, true)]);
        health.Heartbeats[id] = new(Guid.Empty, now);
        var liveRuns = new Dictionary<Guid, Guid> { [id] = Guid.Empty };
        Assert.Equal("operational", health.Evaluate(inventory, false, now, liveRuns).Status);
        health.Evaluate(new(now, [new(id, false, false)]), false, now);
        Assert.Equal("unknown", health.Evaluate(inventory, false, now, liveRuns).Status);
    }

    [Fact]
    public void StoppedAndRestartedAgentsCannotReuseFreshProgressFromAPreviousRun()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var oldRun = Guid.NewGuid();
        var newRun = Guid.NewGuid();
        var health = new AgentProcessHealth();
        var inventory = new AgentInventory(now, [new(id, true, false)]);
        var liveRuns = new Dictionary<Guid, Guid> { [id] = oldRun };
        health.Heartbeats[id] = new(oldRun, now);
        Assert.Equal("operational", health.Evaluate(inventory, false, now, liveRuns).Status);

        liveRuns.Clear();
        Assert.NotEqual("operational", health.Evaluate(inventory, false, now, liveRuns).Status);
        liveRuns[id] = newRun;
        Assert.NotEqual("operational", health.Evaluate(inventory, false, now, liveRuns).Status);
        health.Heartbeats[id] = new(newRun, now);
        Assert.Equal("operational", health.Evaluate(inventory, false, now, liveRuns).Status);
    }
}
