namespace XtremeIdiots.Portal.Server.Agent.App.Parsing;

/// <summary>
/// Log parser for Call of Duty 4: Modern Warfare game servers.
/// CoD4 uses 32-character hexadecimal GUIDs.
/// </summary>
public sealed class Cod4LogParser : CodLogParserBase
{
    private const int Cod4GuidLength = 32;

    /// <inheritdoc />
    protected override bool IsValidGuid(string guid)
    {
        if (guid.Length != Cod4GuidLength)
            return false;

        for (var i = 0; i < guid.Length; i++)
        {
            var c = guid[i];
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }
}
