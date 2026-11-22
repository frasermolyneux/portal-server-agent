using System.Collections.Immutable;

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

internal sealed class PlaytimeProducer
{
    private static readonly TimeSpan MaximumRosterAge = TimeSpan.FromSeconds(10);
    private readonly Guid _serverId;
    private readonly string _gameType;
    private readonly IPlaytimeOutbox _outbox;
    private readonly PlaytimeOutboxDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Guid _epoch = Guid.NewGuid();
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private PlaytimeCoverageState? _coverage;
    private bool _pendingCoverage;
    private long _dropsBeforeCoverageInitialization;
    private long _sequence;
    private PlaytimeObservation? _pending;

    public PlaytimeProducer(Guid serverId, string gameType, IPlaytimeOutbox outbox,
        Func<PlaytimeObservation, CancellationToken, Task> send, ILogger logger)
    {
        if (!SupportsGame(gameType))
        {
            throw new ArgumentException("This game has no agent playtime producer.", nameof(gameType));
        }
        _serverId = serverId;
        _gameType = gameType;
        _outbox = outbox;
        _logger = logger;
        _dispatcher = new PlaytimeOutboxDispatcher(outbox, send, logger);
        SourceTracker = new PlaytimeSourceTracker(outbox, serverId, gameType,
            ct => _dispatcher.DispatchAsync(serverId, ct), () => checked(++_sequence), logger);
    }

    public PlaytimeSourceTracker SourceTracker { get; }
    public bool HasUnpersistedObservation => _pending is not null || _pendingCoverage || SourceTracker.HasPending;

    public static bool SupportsGame(string gameType)
        => string.Equals(gameType, "CallOfDuty2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(gameType, "CallOfDuty4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(gameType, "CallOfDuty5", StringComparison.OrdinalIgnoreCase);

    public Task ObserveRosterAsync(ServerRosterSnapshot? roster, CancellationToken ct)
        => ObserveRosterAsync(roster, dispatch: true, ct);

    public Task<bool> CaptureRosterAsync(ServerRosterSnapshot? roster, CancellationToken ct)
        => ObserveRosterAsync(roster, dispatch: false, ct);

    private async Task<bool> ObserveRosterAsync(ServerRosterSnapshot? roster, bool dispatch, CancellationToken ct)
    {
        if (roster is null || roster.State == RconRosterState.Unknown)
        {
            await RecordGapAsync(PlaytimeGapReason.RosterUnavailable, ct).ConfigureAwait(false);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (roster.ObservedUtc.Kind != DateTimeKind.Utc || roster.CaptureDuration < TimeSpan.Zero
            || roster.CaptureDuration > MaximumRosterAge || now - roster.ObservedUtc > MaximumRosterAge
            || roster.ObservedUtc > now)
        {
            await RecordGapAsync(PlaytimeGapReason.RosterStale, ct).ConfigureAwait(false);
            return false;
        }

        var namedPlayers = roster.Players.Where(player => !string.IsNullOrWhiteSpace(player.Username)).ToArray();
        var missingNames = roster.Players.Count - namedPlayers.Length;
        var completeness = roster.State switch
        {
            RconRosterState.Complete when missingNames == 0 => PlaytimeCompleteness.Complete,
            RconRosterState.Complete => PlaytimeCompleteness.Partial,
            RconRosterState.Partial => PlaytimeCompleteness.Partial,
            RconRosterState.Failed => PlaytimeCompleteness.Failed,
            _ => throw new InvalidOperationException("Unsupported RCON roster state.")
        };
        if (roster.State == RconRosterState.Complete && (roster.UnparsedRowCount != 0 || roster.MissingIdentityCount != 0)
            || roster.Players.Select(player => player.SlotId).Distinct().Count() != roster.Players.Count)
        {
            await RecordGapAsync(PlaytimeGapReason.InvalidRoster, ct).ConfigureAwait(false);
            return false;
        }

        var sequence = checked(++_sequence);
        var observation = new PlaytimeObservation
        {
            EventId = Guid.NewGuid(),
            ServerId = _serverId,
            GameType = _gameType,
            EventGeneratedUtc = now,
            EventPublishedUtc = null,
            EventUtc = new DateTimeOffset(roster.ObservedUtc),
            SequenceId = sequence,
            Kind = PlaytimeEventKind.Roster,
            TimestampQuality = PlaytimeTimestampQuality.RosterSnapshotTimestamp,
            Freshness = PlaytimeFreshness.Unknown,
            Completeness = completeness,
            Epoch = _coverage?.Epoch ?? _epoch,
            ProducerSequence = null,
            Players = namedPlayers.Select(player =>
                new PlaytimeRosterPlayer(player.PlayerGuid, player.Username!, player.SlotId, player.IsBot)).ToImmutableArray()
        };
        try
        {
            observation.Validate();
        }
        catch (ArgumentException)
        {
            await RecordGapAsync(PlaytimeGapReason.InvalidRoster, ct).ConfigureAwait(false);
            return false;
        }

        if (_pending is not null)
        {
            if (_coverage is null)
            {
                _dropsBeforeCoverageInitialization = checked(_dropsBeforeCoverageInitialization + 1);
            }
            else
            {
                _coverage = _coverage with
                {
                    Coverage = _coverage.Coverage with { DroppedCapacity = checked(_coverage.Coverage.DroppedCapacity + 1) }
                };
            }
            _pendingCoverage = true;
            _logger.LogWarning("Playtime roster dropped for server {ServerId}: {Reason}", _serverId, PlaytimeGapReason.Backlog);
            await FlushPendingDropAsync(ct).ConfigureAwait(false);
            return false;
        }

        await SourceTracker.FlushPendingAsync(ct).ConfigureAwait(false);
        _pending = observation;
        SourceTracker.ObserveRoster(roster, completeness);
        await PersistPendingAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Playtime roster captured for server {ServerId} game {GameType}: state {RosterState}, completeness {Completeness}, identities {IdentityCount}, unknown bot status {UnknownBotCount}, unparsed rows {UnparsedRowCount}, missing identities {MissingIdentityCount}, missing names {MissingNameCount}",
            _serverId, _gameType, roster.State, completeness, namedPlayers.Length, namedPlayers.Count(player => player.IsBot is null),
            roster.UnparsedRowCount, roster.MissingIdentityCount, missingNames);
        if (dispatch)
        {
            await _dispatcher.DispatchAsync(_serverId, ct).ConfigureAwait(false);
        }
        return true;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await SourceTracker.FlushPendingAsync(ct).ConfigureAwait(false);
        await PersistPendingAsync(ct).ConfigureAwait(false);
        await _dispatcher.DispatchAsync(_serverId, ct).ConfigureAwait(false);
    }

    private async Task PersistPendingAsync(CancellationToken ct)
    {
        await SourceTracker.FlushPendingAsync(ct).ConfigureAwait(false);
        await FlushPendingDropAsync(ct).ConfigureAwait(false);
        if (_pending is not null)
        {
            if (_pending.Coverage is null)
            {
                _coverage = await _outbox.GetOrCreateCoverageAsync(_serverId, _epoch, _startedUtc, ct).ConfigureAwait(false);
                _pending = _pending with { Epoch = _coverage.Epoch, Coverage = _coverage.Coverage };
            }
            try
            {
                await _outbox.EnqueueAsync(_serverId, _pending, ct).ConfigureAwait(false);
            }
            catch (PlaytimeOutboxCapacityExceededException)
            {
                await _dispatcher.DispatchAsync(_serverId, ct).ConfigureAwait(false);
                await _outbox.EnqueueAsync(_serverId, _pending, ct).ConfigureAwait(false);
            }

            _pending = null;
        }

    }

    private async Task FlushPendingDropAsync(CancellationToken ct)
    {
        if (!_pendingCoverage)
        {
            return;
        }

        _coverage ??= await _outbox.GetOrCreateCoverageAsync(_serverId, _epoch, _startedUtc, ct).ConfigureAwait(false);
        if (_dropsBeforeCoverageInitialization != 0)
        {
            _coverage = _coverage with
            {
                Coverage = _coverage.Coverage with
                {
                    DroppedCapacity = checked(_coverage.Coverage.DroppedCapacity + _dropsBeforeCoverageInitialization)
                }
            };
            _dropsBeforeCoverageInitialization = 0;
        }
        await _outbox.RecordCoverageDropAsync(_serverId, _coverage.Epoch, PlaytimeDropReason.Capacity,
            _coverage.Coverage.DroppedCapacity, ct).ConfigureAwait(false);
        _coverage = await _outbox.GetOrCreateCoverageAsync(_serverId, _epoch, _startedUtc, ct).ConfigureAwait(false);
        _pendingCoverage = false;
    }

    private async Task RecordGapAsync(PlaytimeGapReason reason, CancellationToken ct)
    {
        _logger.LogWarning("Playtime roster coverage gap for server {ServerId} game {GameType}: {Reason}", _serverId, _gameType, reason);
        await _outbox.RecordGapAsync(_serverId, reason, ct).ConfigureAwait(false);
    }

    public static async Task SendAsync(ServiceBusSender sender, PlaytimeObservation observation, CancellationToken ct)
    {
        var message = new ServiceBusMessage(BinaryData.FromString(PlaytimeWireSerializer.Serialize(observation)))
        {
            ContentType = "application/json",
            MessageId = observation.EventId.ToString("D")
        };
        await sender.SendMessageAsync(message, ct).ConfigureAwait(false);
    }
}
