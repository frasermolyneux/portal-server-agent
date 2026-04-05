namespace XtremeIdiots.Portal.Server.Agent.App.Parsing;

/// <summary>
/// Parses raw game server log lines into typed <see cref="GameEvent"/> objects.
/// Maintains internal state (slot map, current map) across calls.
/// </summary>
public interface ILogParser
{
    /// <summary>
    /// Parse a single raw log line into a game event.
    /// Returns null if the line is not a recognised event.
    /// </summary>
    /// <param name="line">A raw line from the game server log file.</param>
    /// <returns>A typed <see cref="GameEvent"/> or null for unrecognised lines.</returns>
    GameEvent? ParseLine(string line);

    /// <summary>
    /// Get the current map name (set when an InitGame event is parsed).
    /// </summary>
    string? CurrentMap { get; }

    /// <summary>
    /// Get a snapshot of currently connected players keyed by slot ID.
    /// </summary>
    IReadOnlyDictionary<int, PlayerInfo> ConnectedPlayers { get; }

    /// <summary>
    /// Reset all parser state (slot map, current map). Call on reconnect.
    /// </summary>
    void Reset();
}

/// <summary>
/// Information about a player currently connected to a game server slot.
/// </summary>
public sealed record PlayerInfo
{
    /// <summary>
    /// Game-specific unique identifier for the player.
    /// </summary>
    public required string Guid { get; init; }

    /// <summary>
    /// Player's display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Client slot number assigned by the game server.
    /// </summary>
    public required int SlotId { get; init; }

    /// <summary>
    /// UTC timestamp when the player connected.
    /// </summary>
    public required DateTime ConnectedAt { get; init; }
}
