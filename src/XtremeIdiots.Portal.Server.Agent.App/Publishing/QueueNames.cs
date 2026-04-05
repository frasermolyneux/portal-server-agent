namespace XtremeIdiots.Portal.Server.Agent.App.Publishing;

/// <summary>
/// Service Bus queue name constants. Mirrors XtremeIdiots.Portal.Server.Events.Abstractions.V1.Queues.
/// Replace with the NuGet package reference when published.
/// </summary>
internal static class QueueNames
{
    public const string PlayerConnected = "player-connected";
    public const string PlayerDisconnected = "player-disconnected";
    public const string ChatMessage = "chat-message";
    public const string MapVote = "map-vote";
    public const string MapChange = "map-change";
    public const string ServerStatus = "server-status";
    public const string ServerConnected = "server-connected";
    public const string BanFileChanged = "ban-file-changed";
}
