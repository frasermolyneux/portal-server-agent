namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Per-game-type ban file path resolution.
///
/// CoD2: <c>{root}/ban.txt</c> (ban file lives at the server root, never under <c>mods/</c>).
/// CoD4 / CoD5: <c>{root}/mods/{liveMod}/ban.txt</c> when a mod is running, otherwise
/// <c>{root}/main/ban.txt</c>. All comparisons are case-insensitive.
/// CoD4x: <c>{root}/banlist_v2.dat</c> (cod4x simplebanlist v2 format) at the
/// configured ban-file root path (never under <c>mods/</c>).
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
            "CallOfDuty4" or "CallOfDuty5" => ResolveCodModPath(normalisedRoot, liveMod, "ban.txt"),

            // CoD4x: banlist_v2.dat is stored directly at the configured ban-file
            // root path, not under mods/<mod>/.
            "CallOfDuty4x" => new ResolvedBanFilePath
            {
                Path = $"{normalisedRoot}banlist_v2.dat",
                ResolvedForMod = null
            },

            // Default: server root. Keeps newly-onboarded game types working until
            // a specific rule is added.
            _ => new ResolvedBanFilePath
            {
                Path = $"{normalisedRoot}ban.txt",
                ResolvedForMod = null
            }
        };
    }

    private static ResolvedBanFilePath ResolveCodModPath(string normalisedRoot, string? liveMod, string fileName)
    {
        // Treat empty or whitespace mod as "no mod" (server may report empty when on the
        // base game) — fall back to main/ which is where the stock ban file lives.
        if (string.IsNullOrWhiteSpace(liveMod))
        {
            return new ResolvedBanFilePath
            {
                Path = $"{normalisedRoot}main/{fileName}",
                ResolvedForMod = "main"
            };
        }

        var trimmedMod = liveMod.Trim().Replace('\\', '/').Trim('/');
        var modFolderToken = "mods";

        // Preserve the folder token exactly as reported by the server when fs_game is
        // provided as "Mods/<mod>" (or any case variant). This keeps path casing
        // accurate for Linux/SFTP targets while still normalising the mod segment.
        var firstSlash = trimmedMod.IndexOf('/');
        if (firstSlash > 0)
        {
            var maybeModsToken = trimmedMod[..firstSlash];
            var remainder = trimmedMod[(firstSlash + 1)..].TrimStart('/');

            if (maybeModsToken.Equals("mods", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(remainder))
            {
                modFolderToken = maybeModsToken;
                trimmedMod = remainder;
            }
        }

        if (string.IsNullOrWhiteSpace(trimmedMod) ||
            trimmedMod.Equals("mods", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedBanFilePath
            {
                Path = $"{normalisedRoot}main/{fileName}",
                ResolvedForMod = "main"
            };
        }

        return new ResolvedBanFilePath
        {
            Path = $"{normalisedRoot}{modFolderToken}/{trimmedMod}/{fileName}",
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
        {
            return "/";
        }

        var trimmed = rootPath.Replace('\\', '/').Trim();
        if (!trimmed.EndsWith('/'))
        {
            trimmed += "/";
        }

        return trimmed;
    }
}
