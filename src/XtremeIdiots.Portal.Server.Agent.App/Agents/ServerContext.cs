namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Configuration for a single game server agent, derived from the server config provider.
/// </summary>
public sealed record ServerContext
{
    public required Guid ServerId { get; init; }
    public required string GameType { get; init; }
    public required string Title { get; init; }

    // FTP config (from "ftp" config namespace)
    public required string FtpHostname { get; init; }
    public required int FtpPort { get; init; }
    public required string FtpUsername { get; init; }
    public required string FtpPassword { get; init; }

    // Agent config (from "agent" config namespace)
    public required string? LogFilePath { get; init; }

    // RCON config (from "rcon" config namespace)
    public required string Hostname { get; init; }
    public required int QueryPort { get; init; }
    public required string? RconPassword { get; init; }

    // Feature toggles
    public required bool FtpEnabled { get; init; }
    public required bool RconEnabled { get; init; }
    public required bool BanFileSyncEnabled { get; init; }

    /// <summary>
    /// SHA256 hash of the server's configuration values.
    /// Used by the orchestrator to detect config changes and restart the agent.
    /// </summary>
    public required string ConfigHash { get; init; }
}
