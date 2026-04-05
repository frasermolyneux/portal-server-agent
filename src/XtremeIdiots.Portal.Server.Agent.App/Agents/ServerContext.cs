namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Configuration for a single game server agent, derived from the server config provider.
/// </summary>
public sealed record ServerContext
{
    public required Guid ServerId { get; init; }
    public required string GameType { get; init; }
    public required string Title { get; init; }

    // FTP config
    public required string FtpHostname { get; init; }
    public required int FtpPort { get; init; }
    public required string FtpUsername { get; init; }
    public required string FtpPassword { get; init; }
    public required string? LiveLogFile { get; init; }

    // RCON config (for future use)
    public required string Hostname { get; init; }
    public required int QueryPort { get; init; }
    public required string? RconPassword { get; init; }
}
