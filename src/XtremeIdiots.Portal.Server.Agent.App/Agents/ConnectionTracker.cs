using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

internal sealed class ConnectionTracker
{
    private readonly Dictionary<int, Connection> _occupants = new();
    private readonly HashSet<string> _uncertainPlayers = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasCompleteBaseline;

    public bool HasCompleteBaseline => _hasCompleteBaseline;

    public void Reset(bool preservePossibleCarryovers = false)
    {
        if (preservePossibleCarryovers)
        {
            foreach (var player in _occupants.Values)
            {
                _uncertainPlayers.Add(player.PlayerGuid);
            }
        }
        else
        {
            _uncertainPlayers.Clear();
        }
        _occupants.Clear();
        _hasCompleteBaseline = false;
    }

    public void ObserveRoster(ServerRosterSnapshot? roster)
    {
        if (roster is not
            {
                State: RconRosterState.Complete,
                UnparsedRowCount: 0,
                MissingIdentityCount: 0,
                KnownBotCount: 0,
                CaptureDuration.TotalSeconds: <= 10
            } ||
            roster.Players.Any(p => p.SlotId < 0 || p.SlotId > 99 || string.IsNullOrWhiteSpace(p.PlayerGuid)) ||
            roster.Players.Select(p => p.SlotId).Distinct().Count() != roster.Players.Count)
        {
            return;
        }

        var current = new Dictionary<int, Connection>();
        foreach (var player in roster.Players)
        {
            var id = _occupants.TryGetValue(player.SlotId, out var previous) &&
                string.Equals(previous.PlayerGuid, player.PlayerGuid, StringComparison.OrdinalIgnoreCase)
                    ? previous.ConnectionId
                    : null;
            current[player.SlotId] = new Connection(player.PlayerGuid, id);
        }

        _occupants.Clear();
        foreach (var (slot, player) in current)
        {
            _occupants.Add(slot, player);
        }
        _hasCompleteBaseline = true;
    }

    public GameEvent Observe(GameEvent gameEvent)
    {
        switch (gameEvent)
        {
            case MapChangeEvent:
                Reset(preservePossibleCarryovers: true);
                return gameEvent;
            case PlayerConnectedEvent connected:
                var existing = _occupants.GetValueOrDefault(connected.SlotId);
                var newConnection = existing is null && _hasCompleteBaseline &&
                    !_uncertainPlayers.Contains(connected.PlayerGuid);
                var id = existing is null
                    ? newConnection ? Guid.NewGuid() : (Guid?)null
                    : string.Equals(existing.PlayerGuid, connected.PlayerGuid, StringComparison.OrdinalIgnoreCase)
                        ? existing.ConnectionId
                        : null;
                _occupants[connected.SlotId] = new Connection(connected.PlayerGuid, id);
                return connected with
                {
                    ConnectionId = id,
                    IpAddress = newConnection ? string.Empty : connected.IpAddress
                };
            case PlayerDisconnectedEvent disconnected:
                _uncertainPlayers.Remove(disconnected.PlayerGuid);
                if (_occupants.TryGetValue(disconnected.SlotId, out var occupant) &&
                    string.Equals(occupant.PlayerGuid, disconnected.PlayerGuid, StringComparison.OrdinalIgnoreCase))
                {
                    _occupants.Remove(disconnected.SlotId);
                    return disconnected with { ConnectionId = occupant.ConnectionId };
                }
                return disconnected;
            case PlayerIpResolvedEvent resolved:
                if (resolved.SlotId is { } slot && _occupants.TryGetValue(slot, out var player) &&
                    string.Equals(player.PlayerGuid, resolved.PlayerGuid, StringComparison.OrdinalIgnoreCase))
                {
                    return resolved with { ConnectionId = player.ConnectionId };
                }
                return resolved;
            default:
                return gameEvent;
        }
    }

    private sealed record Connection(string PlayerGuid, Guid? ConnectionId);
}
