namespace XtremeIdiots.Portal.Server.Agent.App.Parsing;

/// <summary>
/// Log parser for Call of Duty 2 game servers.
/// CoD2 uses 6+ digit numeric GUIDs.
/// </summary>
public sealed class Cod2LogParser : CodLogParserBase
{
    private const int MinCod2GuidLength = 6;

    /// <inheritdoc />
    protected override bool IsValidGuid(string guid)
    {
        if (guid.Length < MinCod2GuidLength)
            return false;

        for (var i = 0; i < guid.Length; i++)
        {
            if (!char.IsDigit(guid[i]))
                return false;
        }

        return true;
    }
}
