using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public sealed class SiegeRefreshScheduleTests
{
    [Fact]
    public void ActiveSessionRunsRosterAtTenSecondsAndStopsOnExpiryOrUnavailable()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new SiegeRefreshSchedule();
        Assert.True(schedule.StateCheckDue(now));
        Assert.False(schedule.RosterCheckDue(now));
        schedule.RecordState(now, now.AddMinutes(2));
        Assert.True(schedule.RosterCheckDue(now));
        schedule.RecordRosterAttempt(now);
        Assert.False(schedule.RosterCheckDue(now.AddSeconds(9)));
        Assert.True(schedule.RosterCheckDue(now.AddSeconds(10)));
        Assert.False(schedule.RosterCheckDue(now.AddMinutes(2)));
        schedule.RecordState(now.AddSeconds(60), null);
        Assert.False(schedule.RosterCheckDue(now.AddSeconds(70)));
    }

    [Fact]
    public void InactiveJoinChecksAreCoalescedAndStateHasOneMinuteCadence()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new SiegeRefreshSchedule();
        schedule.RecordState(now, null);
        Assert.False(schedule.StateCheckDue(now.AddSeconds(9)));
        schedule.ObserveJoin(now.AddSeconds(9));
        Assert.False(schedule.StateCheckDue(now.AddSeconds(9)));
        Assert.True(schedule.StateCheckDue(now.AddSeconds(10)));
        schedule.RecordState(now.AddSeconds(10), null);
        schedule.ObserveJoin(now.AddSeconds(11));
        Assert.False(schedule.StateCheckDue(now.AddSeconds(11)));
        Assert.True(schedule.StateCheckDue(now.AddSeconds(20)));
    }
}
