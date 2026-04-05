namespace XtremeIdiots.Portal.Server.Agent.App.Parsing;

/// <summary>
/// Factory for creating game-specific <see cref="ILogParser"/> instances.
/// </summary>
public interface ILogParserFactory
{
    /// <summary>
    /// Create a new parser instance for the specified game type.
    /// </summary>
    /// <param name="gameType">
    /// The game type identifier (e.g. "CallOfDuty2", "CallOfDuty4", "CallOfDuty5").
    /// </param>
    /// <returns>A new <see cref="ILogParser"/> configured for the game type.</returns>
    /// <exception cref="ArgumentException">Thrown when the game type is not supported.</exception>
    ILogParser Create(string gameType);
}

/// <summary>
/// Default implementation of <see cref="ILogParserFactory"/>.
/// Creates Call of Duty 2, 4, and 5 log parsers.
/// </summary>
public sealed class LogParserFactory : ILogParserFactory
{
    /// <inheritdoc />
    public ILogParser Create(string gameType) => gameType switch
    {
        "CallOfDuty2" => new Cod2LogParser(),
        "CallOfDuty4" => new Cod4LogParser(),
        "CallOfDuty5" => new Cod5LogParser(),
        _ => throw new ArgumentException($"Unsupported game type: {gameType}", nameof(gameType))
    };
}
