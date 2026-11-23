using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

internal enum PlaytimeEventKind
{
    Join = 1,
    Leave = 2,
    Map = 3,
    Roster = 4
}

internal enum PlaytimeTimestampQuality
{
    Unknown = 0,
    ParsedLogTimestamp = 1,
    ProducerTimestamp = 2,
    RosterSnapshotTimestamp = 3,
    ParseTime = 4
}

internal enum PlaytimeFreshness
{
    Unknown = 0,
    Live = 1
}

internal enum PlaytimeCompleteness
{
    NotApplicable = 0,
    Complete = 1,
    Partial = 2,
    Failed = 3
}

internal sealed record PlaytimeCoverage
{
    public required DateTimeOffset ProducerStartedUtc { get; init; }
    public required long DroppedCapacity { get; init; }
    public required long DroppedAttempts { get; init; }
    public required long DroppedAge { get; init; }
}

internal sealed record PlaytimeRosterPlayer(string GameIdentifier, string Username, int Slot, bool? IsBot);

/// <summary>
/// Internal-only, allow-listed playtime data. Live requires independently verified freshness.
/// </summary>
internal sealed record PlaytimeObservation
{
    public required Guid EventId { get; init; }
    public required Guid ServerId { get; init; }
    public required string GameType { get; init; }
    public required DateTimeOffset EventGeneratedUtc { get; init; }
    public required DateTimeOffset? EventPublishedUtc { get; init; }
    public required DateTimeOffset EventUtc { get; init; }
    public required long SequenceId { get; init; }
    public required PlaytimeEventKind Kind { get; init; }
    public required PlaytimeTimestampQuality TimestampQuality { get; init; }
    public required PlaytimeFreshness Freshness { get; init; }
    public required PlaytimeCompleteness Completeness { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Epoch { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ProducerSequence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CorrelationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlaytimeCoverage? Coverage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GameIdentifier { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Slot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsBot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SteamId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MapName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<PlaytimeRosterPlayer> Players { get; init; }

    public void Validate()
    {
        if (EventId == Guid.Empty || ServerId == Guid.Empty)
        {
            throw new ArgumentException("Event and server IDs are required.");
        }

        if (!IsBoundedIdentifier(GameType, 64)
            || EventGeneratedUtc.Offset != TimeSpan.Zero
            || EventPublishedUtc is { Offset: var publishedOffset } && publishedOffset != TimeSpan.Zero
            || EventUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Game type and event timestamps are invalid.");
        }

        if (SequenceId < 0 || ProducerSequence < 0 || Epoch == Guid.Empty || CorrelationId == Guid.Empty
            || ProducerSequence is not null && Epoch is null)
        {
            throw new ArgumentException("Sequence and optional epoch/correlation metadata are invalid.");
        }

        if (!Enum.IsDefined(Kind)
            || !Enum.IsDefined(TimestampQuality)
            || TimestampQuality == PlaytimeTimestampQuality.Unknown
            || !Enum.IsDefined(Freshness)
            || !Enum.IsDefined(Completeness))
        {
            throw new ArgumentException("Observation contains an unsupported enum value.");
        }

        if (Kind == PlaytimeEventKind.Roster
                ? Completeness is not (PlaytimeCompleteness.Complete or PlaytimeCompleteness.Partial or PlaytimeCompleteness.Failed)
                : Completeness != PlaytimeCompleteness.NotApplicable)
        {
            throw new ArgumentException("Roster completeness does not match the event kind.");
        }

        if (Kind == PlaytimeEventKind.Roster
                ? Players.IsDefault || Players.Length > PlaytimeOutboxLimits.MaximumRosterEntries
                : !Players.IsDefault)
        {
            throw new ArgumentException("Roster is invalid or exceeds its bound.", nameof(Players));
        }

        switch (Kind)
        {
            case PlaytimeEventKind.Join:
                ValidatePlayerFields();
                if (Coverage is not null || SteamId is not null && !IsBoundedIdentifier(SteamId, PlaytimeOutboxLimits.MaximumIdentifierLength))
                {
                    throw new ArgumentException("Join correlation is optional and coverage is not allowed.");
                }

                break;

            case PlaytimeEventKind.Leave:
                ValidatePlayerFields();
                if (SteamId is not null || Coverage is not null)
                {
                    throw new ArgumentException("Leave observations do not carry Steam IDs or coverage.");
                }

                break;

            case PlaytimeEventKind.Map:
                if (!IsSafeMapName(MapName)
                    || HasPlayerFields()
                    || SteamId is not null
                    || CorrelationId is not null
                    || Coverage is not null
                    || !Players.IsDefault)
                {
                    throw new ArgumentException("Map observations require only a bounded map identifier.");
                }

                break;

            case PlaytimeEventKind.Roster:
                if (MapName is not null && !IsSafeMapName(MapName)
                    || HasPlayerFields()
                    || SteamId is not null
                    || CorrelationId is not null
                    || Coverage is not null && Epoch is null
                    || !Players.All(IsValidRosterPlayer))
                {
                    throw new ArgumentException("Roster observations require valid roster identities and coverage metadata.");
                }

                if (Coverage is not null)
                {
                    Coverage.Validate(EventUtc);
                    if (EventPublishedUtc is { } publishedUtc && publishedUtc < Coverage.ProducerStartedUtc)
                    {
                        throw new ArgumentException("Coverage publication precedes the producer start.");
                    }
                }
                break;
        }
    }

    private bool HasPlayerFields()
        => GameIdentifier is not null || Username is not null || Slot is not null || IsBot is not null;

    private void ValidatePlayerFields()
    {
        if (!IsBoundedIdentifier(GameIdentifier, PlaytimeOutboxLimits.MaximumIdentifierLength)
            || !IsBoundedIdentifier(Username, PlaytimeOutboxLimits.MaximumUsernameLength)
            || Slot is null or < 0
            || MapName is not null
            || !Players.IsDefault)
        {
            throw new ArgumentException("Join and leave observations require game identifier, username, and slot.");
        }
    }

    private static bool IsValidRosterPlayer(PlaytimeRosterPlayer? player)
        => player is not null
            && IsBoundedIdentifier(player.GameIdentifier, PlaytimeOutboxLimits.MaximumIdentifierLength)
            && IsBoundedIdentifier(player.Username, PlaytimeOutboxLimits.MaximumUsernameLength)
            && player.Slot >= 0;

    private static bool IsBoundedIdentifier(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static bool IsSafeMapName(string? value)
        => value is { Length: > 0 and <= PlaytimeOutboxLimits.MaximumMapNameLength }
            && value.All(character =>
                character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '_' or '-' or '.');
}

internal static class PlaytimeOutboxLimits
{
    public const int MaximumPendingEvents = 128;
    public const int MaximumRosterEntries = 256;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumUsernameLength = 64;
    public const int MaximumMapNameLength = 64;
}

internal static class PlaytimeCoverageValidation
{
    public static void Validate(this PlaytimeCoverage coverage, DateTimeOffset eventUtc)
    {
        if (coverage.ProducerStartedUtc == default
            || coverage.ProducerStartedUtc.Offset != TimeSpan.Zero
            || coverage.DroppedCapacity < 0
            || coverage.DroppedAttempts < 0
            || coverage.DroppedAge < 0
            || coverage.ProducerStartedUtc > eventUtc)
        {
            throw new ArgumentException("Roster coverage values are invalid.", nameof(coverage));
        }
    }
}
