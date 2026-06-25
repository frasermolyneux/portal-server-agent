namespace XtremeIdiots.Portal.Server.Agent.App.Parsing;

/// <summary>
/// Log parser for Call of Duty 4x (CoD4 modded) game servers.
/// CoD4x emits a 19-digit numeric <c>playerid</c> in the GUID field of J;/Q;/say
/// lines; a value of "0" (or any all-zero string) is a sentinel for unknown
/// identity and must be treated as invalid.
/// </summary>
public sealed class Cod4xLogParser : CodLogParserBase
{
    private const int Cod4xPlayerIdLength = 19;

    /// <inheritdoc />
    protected override bool IsValidGuid(string guid)
    {
        if (guid.Length != Cod4xPlayerIdLength)
        {
            return false;
        }

        var allZeros = true;
        for (var i = 0; i < guid.Length; i++)
        {
            var c = guid[i];
            if (c < '0' || c > '9')
            {
                return false;
            }

            if (c != '0')
            {
                allZeros = false;
            }
        }

        return !allZeros;
    }
}
