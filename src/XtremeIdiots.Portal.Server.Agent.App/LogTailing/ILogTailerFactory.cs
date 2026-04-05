using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Agent.App.LogTailing;

/// <summary>
/// Creates <see cref="ILogTailer"/> instances for each game server that needs log tailing.
/// </summary>
public interface ILogTailerFactory
{
    /// <summary>
    /// Creates a new <see cref="ILogTailer"/> instance.
    /// </summary>
    ILogTailer Create();
}

/// <summary>
/// Default factory that creates <see cref="FtpLogTailer"/> instances.
/// </summary>
public sealed class LogTailerFactory : ILogTailerFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Creates a new <see cref="LogTailerFactory"/>.
    /// </summary>
    /// <param name="loggerFactory">Logger factory for creating per-instance loggers.</param>
    public LogTailerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public ILogTailer Create() => new FtpLogTailer(_loggerFactory.CreateLogger<FtpLogTailer>());
}
