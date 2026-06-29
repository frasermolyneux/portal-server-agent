using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class Cod4xCvarProbeTests
{
    private readonly Mock<IServersApiClient> _mockServersApiClient = new();
    private readonly Mock<IVersionedCoD4xRconApi> _mockVersionedCoD4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> _mockCoD4xRconApi = new();
    private readonly TestLogger<Cod4xCvarProbe> _logger = new();
    private readonly Guid _serverId = Guid.NewGuid();

    private ServerContext CreateContext(string gameType = "CallOfDuty4x", bool rconEnabled = true) => new()
    {
        ServerId = _serverId,
        GameType = gameType,
        Title = "Test Server",
        FtpHostname = "ftp.example.com",
        FtpPort = 21,
        FtpUsername = "user",
        FtpPassword = "pass",
        LogFilePath = "/logs/games_mp.log",
        Hostname = "game.example.com",
        QueryPort = 28960,
        RconPassword = "secret",
        FtpEnabled = true,
        RconEnabled = rconEnabled,
        BanFileSyncEnabled = false,
        BanFileRootPath = "/",
        ConfigHash = "test-hash"
    };

    private Cod4xCvarProbe CreateProbe()
    {
        _mockVersionedCoD4xRconApi.Setup(x => x.V1).Returns(_mockCoD4xRconApi.Object);
        _mockServersApiClient.Setup(x => x.CoD4xRcon).Returns(_mockVersionedCoD4xRconApi.Object);

        var services = new ServiceCollection();
        services.AddSingleton(_mockServersApiClient.Object);
        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new Cod4xCvarProbe(scopeFactory, _logger);
    }

    private static ApiResult<string> SuccessResult(string value)
    {
        return new ApiResult<string>(
            HttpStatusCode.OK,
            new ApiResponse<string>(value));
    }

    private static string BuildCvarListOutput(string gLogSync = "1", string gLogTimeStampInSeconds = "1", string logfile = "2")
    {
        return $"g_logSync \"{gLogSync}\"\n" +
               $"g_logTimeStampInSeconds \"{gLogTimeStampInSeconds}\"\n" +
               $"logfile \"{logfile}\"";
    }

    [Fact]
    public async Task ProbeAsync_DoesNothing_WhenGameTypeIsNotCod4x()
    {
        var probe = CreateProbe();

        await probe.ProbeAsync(CreateContext(gameType: "CallOfDuty4"));

        _mockCoD4xRconApi.Verify(
            r => r.CvarList(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeAsync_DoesNothing_WhenRconIsDisabled()
    {
        var probe = CreateProbe();

        await probe.ProbeAsync(CreateContext(rconEnabled: false));

        _mockCoD4xRconApi.Verify(
            r => r.CvarList(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeAsync_ReadsAllProbedCvars_WhenCod4xAndRconEnabled()
    {
        _mockCoD4xRconApi.Setup(r => r.CvarList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(BuildCvarListOutput()));

        var probe = CreateProbe();

        await probe.ProbeAsync(CreateContext());

        _mockCoD4xRconApi.Verify(
            r => r.CvarList(_serverId, It.IsAny<CancellationToken>()),
            Times.Once);

        foreach (var cvar in Cod4xCvarProbe.ProbedCvars)
        {
            Assert.Contains(_logger.Entries, e => e.Message.Contains(cvar, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ProbeAsync_DoesNotLogCod4xCvarMismatch()
    {
        _mockCoD4xRconApi.Setup(r => r.CvarList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(BuildCvarListOutput(gLogSync: "3", gLogTimeStampInSeconds: "1", logfile: "2")));

        var probe = CreateProbe();

        await probe.ProbeAsync(CreateContext());

        Assert.DoesNotContain(
            _logger.Entries,
            e => e.Message.Contains("Cod4xCvarMismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProbeAsync_LogsWarning_WhenApiReturnsFailure()
    {
        var failedResult = new ApiResult<string>(HttpStatusCode.NotFound, new ApiResponse<string>(new ApiError("NOT_FOUND", "missing")));
        _mockCoD4xRconApi.Setup(r => r.CvarList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(failedResult);

        var probe = CreateProbe();

        await probe.ProbeAsync(CreateContext());

        _mockCoD4xRconApi.Verify(r => r.CvarList(_serverId, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(
            _logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("could not read cvarlist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProbeAsync_DoesNotThrow_WhenApiThrows()
    {
        _mockCoD4xRconApi.Setup(r => r.CvarList(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var probe = CreateProbe();

        await probe.ProbeAsync(CreateContext());

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ProbeAsync_RunsOnlyOnce_PerServer()
    {
        _mockCoD4xRconApi.Setup(r => r.CvarList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(BuildCvarListOutput()));

        var probe = CreateProbe();
        var context = CreateContext();

        await probe.ProbeAsync(context);
        await probe.ProbeAsync(context);

        _mockCoD4xRconApi.Verify(
            r => r.CvarList(_serverId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
