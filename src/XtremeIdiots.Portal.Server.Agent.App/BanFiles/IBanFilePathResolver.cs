namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Resolves the FTP path of a game server's ban file from per-game-type rules
/// plus the live mod observed by the parser. Replaces the legacy
/// <c>BanFileMonitor.FilePath</c> manual entry, which was prone to drift
/// whenever an admin changed the active mod.
/// </summary>
public interface IBanFilePathResolver
{
    /// <summary>
    /// Returns the resolved ban file path for a server and the mod that was used
    /// to derive it. <see cref="ResolvedBanFilePath.ResolvedForMod"/> is null when
    /// the game type does not include a mod segment in its path (e.g. CoD2).
    /// </summary>
    /// <param name="gameType">Game type as reported by the server agent (string,
    /// matching <c>ServerContext.GameType</c>).</param>
    /// <param name="rootPath">FTP root path from <c>ServerContext.BanFileRootPath</c>.
    /// Defaults to <c>"/"</c> when not configured.</param>
    /// <param name="liveMod">Mod name as reported by the live parser. May be null
    /// or empty when the server is offline or the mod is not yet known.</param>
    ResolvedBanFilePath Resolve(string gameType, string rootPath, string? liveMod);
}

/// <summary>
/// Result of <see cref="IBanFilePathResolver.Resolve"/>. <see cref="ResolvedForMod"/>
/// is recorded on the BanFileMonitor status row so the dashboard can flag a mismatch
/// between "the mod the watcher targeted" and "the mod the server is currently
/// running".
/// </summary>
public sealed record ResolvedBanFilePath
{
    public required string Path { get; init; }
    public string? ResolvedForMod { get; init; }
}
