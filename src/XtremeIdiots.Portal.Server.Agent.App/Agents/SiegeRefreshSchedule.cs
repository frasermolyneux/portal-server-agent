namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

internal sealed class SiegeRefreshSchedule
{
    internal static readonly TimeSpan StateInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan JoinCheckMinimumInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ActiveRosterInterval = TimeSpan.FromSeconds(10);

    private DateTimeOffset _lastStateCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _nextStateCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRosterCheck = DateTimeOffset.MinValue;
    private DateTimeOffset? _expiresAt;

    public bool IsActive(DateTimeOffset now) => _expiresAt > now;

    public bool StateCheckDue(DateTimeOffset now) => now >= _nextStateCheck;

    public void ObserveJoin(DateTimeOffset now)
    {
        if (!IsActive(now))
        {
            var earliest = _lastStateCheck + JoinCheckMinimumInterval;
            var requested = now > earliest ? now : earliest;
            if (requested < _nextStateCheck)
            {
                _nextStateCheck = requested;
            }
        }
    }

    public void RecordState(DateTimeOffset now, DateTimeOffset? expiresAt)
    {
        var wasActive = IsActive(now);
        _lastStateCheck = now;
        _nextStateCheck = now + StateInterval;
        _expiresAt = expiresAt > now ? expiresAt : null;
        if (!wasActive && IsActive(now))
        {
            _lastRosterCheck = DateTimeOffset.MinValue;
        }
    }

    public bool RosterCheckDue(DateTimeOffset now)
        => IsActive(now) && now - _lastRosterCheck >= ActiveRosterInterval;

    public void RecordRosterAttempt(DateTimeOffset now) => _lastRosterCheck = now;
}
