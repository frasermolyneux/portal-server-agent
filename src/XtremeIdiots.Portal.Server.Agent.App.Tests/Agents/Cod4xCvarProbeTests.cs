using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class Cod4xCvarProbeTests
{
    private readonly Mock<IRconApi> _mockRconApi = new();
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
        var services = new ServiceCollection();
        services.AddSingleton(_mockRconApi.Object);
        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new Cod4xCvarProbe(scopeFactory, _logger);
    }

    private static ApiResult<DvarValueDto> SuccessResult(string name, string value)
    {
        return new ApiResult<DvarValueDto>(
            HttpStatusCode.OK,
            new ApiResponse<DvarValueDto>(new DvarValueDto(name, value)));
    }

    [Fact]
    public async Task ProbeAsync_DoesNothing_WhenGameTypeIsNotCod4x()
    {
        // Arrange
        var probe = CreateProbe();

        // Act
        await probe.ProbeAsync(CreateContext(gameType: "CallOfDuty4"));

        // Assert — RCON should never be called
        _mockRconApi.Verify(
            r => r.GetDvar(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeAsync_DoesNothing_WhenRconIsDisabled()
    {
        // Arrange
        var probe = CreateProbe();

        // Act
        await probe.ProbeAsync(CreateContext(rconEnabled: false));

        // Assert
        _mockRconApi.Verify(
            r => r.GetDvar(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeAsync_ReadsAllProbedCvars_WhenCod4xAndRconEnabled()
    {
        // Arrange — return a benign success for every cvar
        foreach (var cvar in Cod4xCvarProbe.ProbedCvars)
        {
            var localCvar = cvar;
            _mockRconApi.Setup(r => r.GetDvar(_serverId, localCvar, It.IsAny<CancellationToken>()))
                .ReturnsAsync(SuccessResult(localCvar, "1"));
        }

        var probe = CreateProbe();

        // Act
        await probe.ProbeAsync(CreateContext());

        // Assert — every cvar in the locked matrix is read exactly once
        foreach (var cvar in Cod4xCvarProbe.ProbedCvars)
        {
            _mockRconApi.Verify(
                r => r.GetDvar(_serverId, cvar, It.IsAny<CancellationToken>()),
                Times.Once,
                $"Expected probe to read {cvar} exactly once");
        }
    }

    [Fact]
    public async Task ProbeAsync_DoesNotLogCod4xCvarMismatch()
    {
        // Arrange — every cvar returns benign values
        _mockRconApi.Setup(r => r.GetDvar(_serverId, "g_logSync", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("g_logSync", "3"));
        _mockRconApi.Setup(r => r.GetDvar(_serverId, "g_logTimeStampInSeconds", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("g_logTimeStampInSeconds", "1"));
        _mockRconApi.Setup(r => r.GetDvar(_serverId, "logfile", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("logfile", "2"));

        var probe = CreateProbe();

        // Act
        await probe.ProbeAsync(CreateContext());

        // Assert — no Cod4xCvarMismatch warning was emitted
        Assert.DoesNotContain(
            _logger.Entries,
            e => e.Message.Contains("Cod4xCvarMismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProbeAsync_LogsWarningAndContinues_WhenApiReturnsFailure()
    {
        // Arrange — one cvar read fails, the others succeed
        var failedResult = new ApiResult<DvarValueDto>(HttpStatusCode.NotFound, null);
        _mockRconApi.Setup(r => r.GetDvar(_serverId, "g_logSync", It.IsAny<CancellationToken>()))
            .ReturnsAsync(failedResult);
        _mockRconApi.Setup(r => r.GetDvar(_serverId, "g_logTimeStampInSeconds", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("g_logTimeStampInSeconds", "1"));
        _mockRconApi.Setup(r => r.GetDvar(_serverId, "logfile", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("logfile", "2"));

        var probe = CreateProbe();

        // Act
        await probe.ProbeAsync(CreateContext());

        // Assert — all probed cvars were attempted (probe did not abort)
        foreach (var cvar in Cod4xCvarProbe.ProbedCvars)
        {
            _mockRconApi.Verify(
                r => r.GetDvar(_serverId, cvar, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // And the failure was logged as a warning
        Assert.Contains(
            _logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("could not read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProbeAsync_DoesNotThrow_WhenApiThrows()
    {
        // Arrange — GetDvar throws on every call
        _mockRconApi.Setup(r => r.GetDvar(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var probe = CreateProbe();

        // Act + Assert — no exception escapes
        await probe.ProbeAsync(CreateContext());

        // Failures were logged as warnings (per-cvar)
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ProbeAsync_RunsOnlyOnce_PerServer()
    {
        // Arrange — every probed cvar returns a benign value
        foreach (var cvar in Cod4xCvarProbe.ProbedCvars)
        {
            _mockRconApi.Setup(r => r.GetDvar(_serverId, cvar, It.IsAny<CancellationToken>()))
                .ReturnsAsync(SuccessResult(cvar, "1"));
        }

        var probe = CreateProbe();
        var context = CreateContext();

        // Act — call the probe twice
        await probe.ProbeAsync(context);
        await probe.ProbeAsync(context);

        // Assert — each cvar was only read once across both calls
        foreach (var cvar in Cod4xCvarProbe.ProbedCvars)
        {
            _mockRconApi.Verify(
                r => r.GetDvar(_serverId, cvar, It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    /// <summary>
    /// Lightweight <see cref="ILogger{T}"/> recorder for assertion. Captures level +
    /// formatted message so tests can introspect what the probe logged without needing
    /// a full mocking ceremony around the generic Log signature.
    /// </summary>
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
