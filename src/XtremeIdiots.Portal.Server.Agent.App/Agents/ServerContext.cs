namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public static class FileTransportTypes
{
    public const string Ftp = "ftp";
    public const string Sftp = "sftp";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.Equals(normalized, Sftp, StringComparison.OrdinalIgnoreCase) ? Sftp : Ftp;
    }

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;

        var candidate = value?.Trim();
        if (string.Equals(candidate, Ftp, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Ftp;
            return true;
        }

        if (string.Equals(candidate, Sftp, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Sftp;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Configuration for a single game server agent, derived from the server config provider.
/// </summary>
public sealed record ServerContext
{
    public const int DefaultBroadcastIntervalSeconds = 500;
    public const int DefaultBanFileCheckIntervalSeconds = 60;
    public const string DefaultAgentNamePrefix = "^4[^1>XI< BOT^4]^7";
    public const string DefaultScreenshotFilePattern = "*.jpg";
    public const int DefaultScreenshotPollIntervalSeconds = 60;
    public const int MinScreenshotPollIntervalSeconds = 10;
    public const int MaxScreenshotPollIntervalSeconds = 300;

    public required Guid ServerId { get; init; }
    public required string GameType { get; init; }
    public required string Title { get; init; }

    // Legacy transport-shaped fields retained for compatibility with existing callers/tests.
    public required string FtpHostname { get; init; }
    public required int FtpPort { get; init; }
    public required string FtpUsername { get; init; }
    public required string FtpPassword { get; init; }

    // Transport-aware file configuration (authoritative when provided).
    public bool? FileTransportEnabled { get; init; }
    public string? FileTransportType { get; init; }
    public string? FileTransportHostname { get; init; }
    public int? FileTransportPort { get; init; }
    public string? FileTransportUsername { get; init; }
    public string? FileTransportPassword { get; init; }
    public string? FileTransportHostKeyFingerprint { get; init; }

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
    /// File transport path prefix on the game server below which the ban file lives. Resolved
    /// from <c>GameServer.BanFileRootPath</c>; defaults to <c>"/"</c> for legacy
    /// servers that have not yet had the root path set.
    /// </summary>
    public required string BanFileRootPath { get; init; }

    /// <summary>
    /// Ban file monitor cadence in seconds (from the "banfiles" namespace).
    /// Defaults to the legacy 60-second cadence when unspecified.
    /// </summary>
    public int BanFileCheckIntervalSeconds { get; init; } = DefaultBanFileCheckIntervalSeconds;

    /// <summary>
    /// SHA256 hash of the server's configuration values.
    /// Used by the orchestrator to detect config changes and restart the agent.
    /// </summary>
    public required string ConfigHash { get; init; }

    /// <summary>
    /// Prefix prepended to each broadcast message. Resolved from per-server override
    /// first, then global configuration fallback.
    /// </summary>
    public string AgentNamePrefix { get; init; } = DefaultAgentNamePrefix;

    public string EffectiveFileTransportType => FileTransportTypes.Normalize(FileTransportType);

    public bool EffectiveFileTransportEnabled => FileTransportEnabled ?? false;

    public string EffectiveFileTransportHostname => FileTransportHostname ?? FtpHostname;

    public int EffectiveFileTransportPort
    {
        get
        {
            if (FileTransportPort.HasValue)
            {
                return FileTransportPort.Value;
            }

            return string.Equals(EffectiveFileTransportType, FileTransportTypes.Sftp, StringComparison.OrdinalIgnoreCase)
                ? 22
                : FtpPort;
        }
    }

    public string EffectiveFileTransportUsername => FileTransportUsername ?? FtpUsername;

    public string EffectiveFileTransportPassword => FileTransportPassword ?? FtpPassword;

    /// <summary>
    /// Broadcast message scheduling configuration (from "broadcasts" namespace).
    /// </summary>
    public BroadcastSettings Broadcasts { get; init; } = new();

    /// <summary>
    /// Screenshot monitoring configuration (from "screenshots" namespace).
    /// </summary>
    public ScreenshotSettings Screenshots { get; init; } = new();
}

public sealed record BroadcastSettings
{
    public bool Enabled { get; init; }
    public int IntervalSeconds { get; init; } = ServerContext.DefaultBroadcastIntervalSeconds;
    public IReadOnlyList<BroadcastMessage> Messages { get; init; } = Array.Empty<BroadcastMessage>();
}

public sealed record BroadcastMessage
{
    public required string Message { get; init; }
    public bool Enabled { get; init; }
}

public sealed record ScreenshotSettings
{
    public bool Enabled { get; init; }
    public string DirectoryPath { get; init; } = string.Empty;
    public string FilePattern { get; init; } = ServerContext.DefaultScreenshotFilePattern;
    public int PollIntervalSeconds { get; init; } = ServerContext.DefaultScreenshotPollIntervalSeconds;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
