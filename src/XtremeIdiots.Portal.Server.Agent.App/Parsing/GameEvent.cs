namespace XtremeIdiots.Portal.Server.Agent.App.Parsing;

/// <summary>
/// Base type for all parsed game events. Concrete subtypes represent specific
/// actions detected in the game server log.
/// </summary>
public abstract record GameEvent
{
    /// <summary>
    /// UTC timestamp indicating when the event was parsed (log lines lack date information).
    /// </summary>
    public required DateTime Timestamp { get; init; }
}

/// <summary>
/// A player connected to the game server.
/// </summary>
public sealed record PlayerConnectedEvent : GameEvent
{
    /// <summary>
    /// Game-specific unique identifier for the player.
    /// </summary>
    public required string PlayerGuid { get; init; }

    /// <summary>
    /// Player's display name at the time of connection.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Player's IP address if known (e.g. from a prior RCON sync), otherwise empty.
    /// </summary>
    public string IpAddress { get; init; } = string.Empty;

    /// <summary>
    /// Client slot number assigned by the game server.
    /// </summary>
    public required int SlotId { get; init; }
}

/// <summary>
/// A player disconnected from the game server.
/// </summary>
public sealed record PlayerDisconnectedEvent : GameEvent
{
    /// <summary>
    /// Game-specific unique identifier for the player.
    /// </summary>
    public required string PlayerGuid { get; init; }

    /// <summary>
    /// Player's display name at the time of disconnection.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Client slot number the player occupied.
    /// </summary>
    public required int SlotId { get; init; }
}

/// <summary>
/// A chat message sent by a player.
/// </summary>
public sealed record ChatMessageEvent : GameEvent
{
    /// <summary>
    /// Game-specific unique identifier for the player who sent the message.
    /// </summary>
    public required string PlayerGuid { get; init; }

    /// <summary>
    /// Player's display name.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// The chat message content.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// True if the message was sent to the player's team only.
    /// </summary>
    public required bool IsTeamChat { get; init; }
}

/// <summary>
/// The server changed map (detected via InitGame).
/// </summary>
public sealed record MapChangeEvent : GameEvent
{
    /// <summary>
    /// The BSP map name (e.g. mp_crash, mp_crossfire).
    /// </summary>
    public required string MapName { get; init; }

    /// <summary>
    /// The game type string (e.g. tdm, sd, dm).
    /// </summary>
    public required string GameType { get; init; }
}
