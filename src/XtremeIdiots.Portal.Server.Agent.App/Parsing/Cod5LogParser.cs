using System.Text.RegularExpressions;

namespace XtremeIdiots.Portal.Server.Agent.App.Parsing;

/// <summary>
/// Log parser for Call of Duty: World at War (CoD5) game servers.
/// CoD5 uses 8+ digit numeric GUIDs and supports JoinTeam (JT) events.
/// </summary>
public sealed class Cod5LogParser : CodLogParserBase
{
    private const int MinCod5GuidLength = 8;

    private static readonly Regex JoinTeamPattern = new(
        @"^JT;(?<guid>[^;]+);(?<cid>\d{1,2});(?<team>[^;]*);(?<name>[^;]*);?$",
        RegexOptions.Compiled);

    /// <inheritdoc />
    protected override bool IsValidGuid(string guid)
    {
        if (guid.Length < MinCod5GuidLength)
            return false;

        for (var i = 0; i < guid.Length; i++)
        {
            if (!char.IsDigit(guid[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Handle JT (JoinTeam) events specific to CoD5. If the player is not
    /// already tracked in the slot map, they are added (treated as a join).
    /// </summary>
    protected override GameEvent? HandleJoinTeam(Match match, DateTime timestamp)
    {
        var guid = match.Groups["guid"].Value;
        var cidStr = match.Groups["cid"].Value;
        var name = match.Groups["name"].Value;

        if (!int.TryParse(cidStr, out var cid))
            return null;

        if (!IsValidGuid(guid))
            return null;

        if (!HasPlayerInSlot(cid))
        {
            var playerInfo = new PlayerInfo
            {
                Guid = guid,
                Name = name,
                SlotId = cid,
                ConnectedAt = timestamp
            };

            UpdateSlotMap(cid, playerInfo);

            return new PlayerConnectedEvent
            {
                Timestamp = timestamp,
                PlayerGuid = guid,
                Username = name,
                SlotId = cid
            };
        }

        // Player already tracked — JoinTeam is just a team change, no event emitted
        return null;
    }
}
