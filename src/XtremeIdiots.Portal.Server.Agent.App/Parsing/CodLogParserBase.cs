using System.Text.RegularExpressions;

namespace XtremeIdiots.Portal.Server.Agent.App.Parsing;

/// <summary>
/// Shared parsing logic for all Call of Duty game server logs.
/// Game-specific subclasses override GUID validation only.
/// </summary>
public abstract class CodLogParserBase : ILogParser
{
    // --- Compiled regex patterns (shared across all instances) ---

    private static readonly Regex TimestampRegex = new(
        @"^\s*\d+:\d+\s*",
        RegexOptions.Compiled);

    private static readonly Regex JoinRegex = new(
        @"^J;(?<guid>[^;]+);(?<cid>\d{1,2});(?<name>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex QuitRegex = new(
        @"^Q;(?<guid>[^;]+);(?<cid>\d{1,2});(?<name>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex SayRegex = new(
        @"^(?<action>sayteam|say);(?<guid>[^;]+);(?<cid>\d{1,2});(?<name>[^;]+);(?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex JoinTeamRegex = new(
        @"^JT;(?<guid>[^;]+);(?<cid>\d{1,2});(?<team>[^;]*);(?<name>[^;]*);?$",
        RegexOptions.Compiled);

    private static readonly Regex KillDamageRegex = new(
        @"^[KD];",
        RegexOptions.Compiled);

    private static readonly Regex ServerEventRegex = new(
        @"^(?<action>\w+):\s*(?<data>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex InitGameKvRegex = new(
        @"\\([^\\]+)\\([^\\]+)",
        RegexOptions.Compiled);

    // --- State ---

    private readonly Dictionary<int, PlayerInfo> _slotMap = new();
    private string? _currentMap;
    private string? _currentGameType;

    /// <inheritdoc />
    public string? CurrentMap => _currentMap;

    /// <inheritdoc />
    public IReadOnlyDictionary<int, PlayerInfo> ConnectedPlayers => _slotMap;

    /// <summary>
    /// Validate that the GUID format is correct for this game variant.
    /// </summary>
    /// <param name="guid">The raw GUID string from the log line.</param>
    /// <returns>True if the GUID is valid for this game.</returns>
    protected abstract bool IsValidGuid(string guid);

    /// <inheritdoc />
    public GameEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Strip timestamp prefix (e.g. "  3:42 " or "123:45 ")
        var cleaned = TimestampRegex.Replace(line, string.Empty);
        if (string.IsNullOrEmpty(cleaned))
            return null;

        var timestamp = DateTime.UtcNow;

        // Try server events first (InitGame:, ShutdownGame:, ExitLevel:)
        var serverMatch = ServerEventRegex.Match(cleaned);
        if (serverMatch.Success)
        {
            return HandleServerEvent(serverMatch, timestamp);
        }

        // Try player/action events
        return HandlePlayerEvent(cleaned, timestamp);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _slotMap.Clear();
        _currentMap = null;
        _currentGameType = null;
    }

    /// <summary>
    /// Handle server-level events (InitGame, ShutdownGame, ExitLevel).
    /// </summary>
    private GameEvent? HandleServerEvent(Match match, DateTime timestamp)
    {
        var action = match.Groups["action"].Value;
        var data = match.Groups["data"].Value;

        if (string.Equals(action, "InitGame", StringComparison.OrdinalIgnoreCase))
        {
            return HandleInitGame(data, timestamp);
        }

        // ShutdownGame and ExitLevel are currently not mapped to events
        return null;
    }

    /// <summary>
    /// Parse InitGame key-value pairs, update current map/gametype, and clear the slot map.
    /// </summary>
    private GameEvent? HandleInitGame(string data, DateTime timestamp)
    {
        var kvMatches = InitGameKvRegex.Matches(data);
        string? mapName = null;
        string? gameType = null;

        foreach (Match kv in kvMatches)
        {
            var key = kv.Groups[1].Value;
            var value = kv.Groups[2].Value;

            if (string.Equals(key, "mapname", StringComparison.OrdinalIgnoreCase))
                mapName = value;
            else if (string.Equals(key, "g_gametype", StringComparison.OrdinalIgnoreCase))
                gameType = value;
        }

        // Clear the slot map — all players will re-join after a map change
        _slotMap.Clear();

        _currentMap = mapName;
        _currentGameType = gameType;

        if (mapName is not null && gameType is not null)
        {
            return new MapChangeEvent
            {
                Timestamp = timestamp,
                MapName = mapName,
                GameType = gameType
            };
        }

        return null;
    }

    /// <summary>
    /// Handle player-level events (J, Q, say, sayteam, JT, K, D).
    /// </summary>
    private GameEvent? HandlePlayerEvent(string cleaned, DateTime timestamp)
    {
        // Join
        var joinMatch = JoinRegex.Match(cleaned);
        if (joinMatch.Success)
        {
            return HandleJoin(joinMatch, timestamp);
        }

        // Quit
        var quitMatch = QuitRegex.Match(cleaned);
        if (quitMatch.Success)
        {
            return HandleQuit(quitMatch, timestamp);
        }

        // Say / Sayteam
        var sayMatch = SayRegex.Match(cleaned);
        if (sayMatch.Success)
        {
            return HandleSay(sayMatch, timestamp);
        }

        // JoinTeam (CoD5)
        var jtMatch = JoinTeamRegex.Match(cleaned);
        if (jtMatch.Success)
        {
            return HandleJoinTeam(jtMatch, timestamp);
        }

        // Kill and Damage lines are recognised but not currently mapped to events
        // (they are silently consumed to avoid polluting unrecognised line handling)

        return null;
    }

    /// <summary>
    /// Handle a J (Join) event: validate GUID, update slot map, return event.
    /// </summary>
    private GameEvent? HandleJoin(Match match, DateTime timestamp)
    {
        var guid = match.Groups["guid"].Value;
        var cidStr = match.Groups["cid"].Value;
        var name = match.Groups["name"].Value;

        if (!int.TryParse(cidStr, out var cid))
            return null;

        if (!IsValidGuid(guid))
            return null;

        var playerInfo = new PlayerInfo
        {
            Guid = guid,
            Name = name,
            SlotId = cid,
            ConnectedAt = timestamp
        };

        _slotMap[cid] = playerInfo;

        return new PlayerConnectedEvent
        {
            Timestamp = timestamp,
            PlayerGuid = guid,
            Username = name,
            SlotId = cid
        };
    }

    /// <summary>
    /// Handle a Q (Quit) event: remove from slot map, return event.
    /// </summary>
    private GameEvent? HandleQuit(Match match, DateTime timestamp)
    {
        var guid = match.Groups["guid"].Value;
        var cidStr = match.Groups["cid"].Value;
        var name = match.Groups["name"].Value;

        if (!int.TryParse(cidStr, out var cid))
            return null;

        if (!IsValidGuid(guid))
            return null;

        _slotMap.Remove(cid);

        return new PlayerDisconnectedEvent
        {
            Timestamp = timestamp,
            PlayerGuid = guid,
            Username = name,
            SlotId = cid
        };
    }

    /// <summary>
    /// Handle say/sayteam events. Always emits a <see cref="ChatMessageEvent"/>.
    /// Command detection (e.g. !like/!dislike) is handled downstream by the event processor.
    /// </summary>
    private GameEvent? HandleSay(Match match, DateTime timestamp)
    {
        var action = match.Groups["action"].Value;
        var guid = match.Groups["guid"].Value;
        var name = match.Groups["name"].Value;
        var text = match.Groups["text"].Value;

        if (!IsValidGuid(guid))
            return null;

        var isTeamChat = string.Equals(action, "sayteam", StringComparison.OrdinalIgnoreCase);

        // Strip the control character (char 21 / NAK) from start of message if present
        if (text.Length > 0 && text[0] == '\x15')
            text = text[1..];

        return new ChatMessageEvent
        {
            Timestamp = timestamp,
            PlayerGuid = guid,
            Username = name,
            Message = text,
            IsTeamChat = isTeamChat
        };
    }

    /// <summary>
    /// Handle JT (JoinTeam) events. If the player is not already tracked,
    /// adds them to the slot map (acts like a join).
    /// </summary>
    protected virtual GameEvent? HandleJoinTeam(Match match, DateTime timestamp)
    {
        // Default: ignore JoinTeam. CoD5 parser overrides this.
        return null;
    }

    /// <summary>
    /// Add or update a player in the slot map. Used by subclass JoinTeam handling.
    /// </summary>
    protected void UpdateSlotMap(int slotId, PlayerInfo playerInfo)
    {
        _slotMap[slotId] = playerInfo;
    }

    /// <summary>
    /// Check whether a player is currently tracked in the specified slot.
    /// </summary>
    protected bool HasPlayerInSlot(int slotId) => _slotMap.ContainsKey(slotId);

    /// <summary>
    /// Get player info for a slot, or null if the slot is empty.
    /// </summary>
    protected PlayerInfo? GetPlayerInSlot(int slotId) =>
        _slotMap.TryGetValue(slotId, out var info) ? info : null;
}
