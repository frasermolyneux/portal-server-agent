namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Provides access to the central regenerated ban file for a given game type.
/// Backed by Azure Blob Storage in production; abstracted for testability.
/// </summary>
public interface IBanFileSource
{
    /// <summary>
    /// Fetches the current central ban file for <paramref name="gameType"/>.
    /// Returns <c>null</c> when the blob does not exist (e.g. no bans yet for the game).
    /// The returned <see cref="CentralBanFile.Content"/> stream is owned by the caller and must be disposed.
    /// </summary>
    Task<CentralBanFile?> GetAsync(string gameType, CancellationToken ct = default);
}

/// <summary>
/// A snapshot of the central ban file, with enough identity to detect changes between polls.
/// </summary>
public sealed class CentralBanFile : IAsyncDisposable, IDisposable
{
    public required string ETag { get; init; }
    public required long Length { get; init; }
    public required Stream Content { get; init; }

    public void Dispose() => Content.Dispose();

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
