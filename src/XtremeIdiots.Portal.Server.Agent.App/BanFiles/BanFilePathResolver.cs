namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Per-game-type ban file path resolution.
///
/// CoD2: <c>{root}/ban.txt</c> (ban file lives at the server root, never under <c>mods/</c>).
/// CoD4 / CoD5: <c>{root}/mods/{liveMod}/ban.txt</c> when a mod is running, otherwise
/// <c>{root}/main/ban.txt</c>. All comparisons are case-insensitive.
///
/// Unknown game types fall back to <c>{root}/ban.txt</c> (the safest assumption).
/// Add new rules here as new game types are onboarded — keep the implementation
/// pure so it can be unit-tested without any FTP or repository dependency.
/// </summary>
public sealed class BanFilePathResolver : IBanFilePathResolver
{
    public ResolvedBanFilePath Resolve(string gameType, string rootPath, string? liveMod)
    {
        var normalisedRoot = NormaliseRoot(rootPath);

        return gameType switch
        {
            // CoD2: ban file is always at server root, never inside a mod folder.
            "CallOfDuty2" => new ResolvedBanFilePath
            {
                Path = $"{normalisedRoot}ban.txt",
                ResolvedForMod = null
            },

            // CoD4 / CoD5: under mods/<mod>/ when a mod is active, else main/.
            "CallOfDuty4" or "CallOfDuty5" => ResolveCodModPath(normalisedRoot, liveMod),

            // Default: server root. Keeps newly-onboarded game types working until
            // a specific rule is added.
            _ => new ResolvedBanFilePath
            {
                Path = $"{normalisedRoot}ban.txt",
                ResolvedForMod = null
            }
        };
    }

    private static ResolvedBanFilePath ResolveCodModPath(string normalisedRoot, string? liveMod)
    {
        // Treat empty or whitespace mod as "no mod" (server may report empty when on the
        // base game) — fall back to main/ which is where the stock ban.txt lives.
        if (string.IsNullOrWhiteSpace(liveMod))
        {
            return new ResolvedBanFilePath
            {
                Path = $"{normalisedRoot}main/ban.txt",
                ResolvedForMod = "main"
            };
        }

        var trimmedMod = liveMod.Trim();

        // Strip a leading "mods/" prefix if the parser ever surfaces one — paths in the
        // ban file are always relative to the mods/ directory.
        if (trimmedMod.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            trimmedMod = trimmedMod[5..];

        return new ResolvedBanFilePath
        {
            Path = $"{normalisedRoot}mods/{trimmedMod}/ban.txt",
            ResolvedForMod = trimmedMod
        };
    }

    /// <summary>
    /// Returns the root path with a trailing slash and any backslashes converted to
    /// forward slashes. Empty / null root becomes <c>"/"</c>.
    /// </summary>
    private static string NormaliseRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return "/";

        var trimmed = rootPath.Replace('\\', '/').Trim();
        if (!trimmed.EndsWith('/'))
            trimmed += "/";
        return trimmed;
    }
}
