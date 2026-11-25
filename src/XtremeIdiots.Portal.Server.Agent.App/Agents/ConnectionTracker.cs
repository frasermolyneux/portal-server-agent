using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

internal sealed class ConnectionTracker
{
    private readonly Dictionary<int, Connection> _occupants = new();
    private bool _hasObservedRoster;

    public bool HasObservedRoster => _hasObservedRoster;

    public Guid? GetConnectionId(int slotId, string playerGuid)
        => _occupants.TryGetValue(slotId, out var player) &&
            string.Equals(player.PlayerGuid, playerGuid, StringComparison.OrdinalIgnoreCase)
                ? player.ConnectionId
                : null;

    public void Reset(bool preservePossibleCarryovers = false)
    {
        if (!preservePossibleCarryovers)
        {
            _occupants.Clear();
        }
        _hasObservedRoster = false;
    }

    public void ObserveRoster(ServerRosterSnapshot? roster, bool establishBaseline = true)
    {
        if (roster is not
            {
                State: RconRosterState.Complete or RconRosterState.Partial,
                CaptureDuration.TotalSeconds: <= 10
            })
        {
            return;
        }

        var uniquePlayers = roster.Players
            .Where(p => IsValidIdentity(p.SlotId, p.PlayerGuid))
            .GroupBy(p => p.SlotId)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();

        foreach (var player in uniquePlayers)
        {
            GetOrCreate(player.SlotId, player.PlayerGuid);
        }

        if (establishBaseline && roster.State == RconRosterState.Complete &&
            roster.UnparsedRowCount == 0 && roster.MissingIdentityCount == 0 && roster.KnownBotCount == 0 &&
            uniquePlayers.Length == roster.Players.Count)
        {
            var slots = uniquePlayers.Select(p => p.SlotId).ToHashSet();
            foreach (var slot in _occupants.Keys.Where(slot => !slots.Contains(slot)).ToArray())
            {
                _occupants.Remove(slot);
            }
        }
        _hasObservedRoster = true;
    }

    public GameEvent Observe(GameEvent gameEvent)
    {
        switch (gameEvent)
        {
            case MapChangeEvent:
                Reset(preservePossibleCarryovers: true);
                return gameEvent;
            case PlayerConnectedEvent connected when IsValidIdentity(connected.SlotId, connected.PlayerGuid):
                var existing = _occupants.GetValueOrDefault(connected.SlotId);
                var newConnection = existing is null ||
                    !string.Equals(existing.PlayerGuid, connected.PlayerGuid, StringComparison.OrdinalIgnoreCase);
                var id = GetOrCreate(connected.SlotId, connected.PlayerGuid);
                return connected with
                {
                    ConnectionId = id,
                    IpAddress = newConnection ? string.Empty : connected.IpAddress
                };
            case PlayerDisconnectedEvent disconnected:
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

    private Guid GetOrCreate(int slotId, string playerGuid)
    {
        if (_occupants.TryGetValue(slotId, out var existing) &&
            string.Equals(existing.PlayerGuid, playerGuid, StringComparison.OrdinalIgnoreCase))
        {
            return existing.ConnectionId;
        }

        var id = Guid.NewGuid();
        _occupants[slotId] = new Connection(playerGuid, id);
        return id;
    }

    private static bool IsValidIdentity(int slotId, string playerGuid)
        => slotId is >= 0 and <= 99 && !string.IsNullOrWhiteSpace(playerGuid);

    private sealed record Connection(string PlayerGuid, Guid ConnectionId);
}
