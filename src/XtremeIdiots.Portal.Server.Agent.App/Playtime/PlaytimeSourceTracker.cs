using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

/// <summary>Produces read-time observations only after the current source's initial EOF.</summary>
internal sealed class PlaytimeSourceTracker
{
    private static readonly Regex JoinTeamPattern = new(
        @"^\s*(?:\d+(?::\d+)?\s+)?JT;(?<guid>[^;]+);(?<slot>\d{1,2});",
        RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex QuitPattern = new(
        @"^\s*(?:\d+(?::\d+)?\s+)?Q;[^;]+;(?<slot>\d{1,2});",
        RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private readonly IPlaytimeOutbox _outbox;
    private readonly Guid _serverId;
    private readonly string _gameType;
    private readonly ILogParser _parser;
    private readonly ILogger? _logger;
    private readonly Func<CancellationToken, Task> _dispatch;
    private readonly Func<long> _nextSequence;
    private readonly Dictionary<int, string> _players = [];
    private readonly HashSet<int> _mapReentries = [];
    private readonly Queue<SourceBatch> _pending = [];
    private PlaytimeSourceCursor? _cursor;
    private long _continuityVersion;
    private long _boundary;
    private bool _boundaryUncertain;
    private PlaytimeGapReason? _waitingForEnd;

    public PlaytimeSourceTracker(IPlaytimeOutbox outbox, Guid serverId, string gameType,
        Func<CancellationToken, Task> dispatch, Func<long> nextSequence, ILogger? logger = null)
    {
        _outbox = outbox;
        _serverId = serverId;
        _gameType = gameType;
        _dispatch = dispatch;
        _nextSequence = nextSequence;
        _logger = logger;
        _parser = gameType.ToLowerInvariant() switch
        {
            "callofduty2" => new Cod2LogParser(),
            "callofduty4" => new Cod4LogParser(),
            "callofduty5" => new Cod5LogParser(),
            _ => throw new ArgumentException("Unsupported playtime log source.", nameof(gameType))
        };
    }

    public bool HasPending => _pending.Count != 0;

    public async Task InitializeAsync(ILogTailer tailer, CancellationToken ct)
    {
        var path = tailer.CurrentFilePath ?? throw new InvalidOperationException("The log tailer has no file path.");
        var next = new PlaytimeSourceCursor(Guid.NewGuid(), path, tailer.InitialEndOffset);
        var previous = await _outbox.GetSourceCursorAsync(_serverId, ct).ConfigureAwait(false);
        if (previous is null)
        {
            await _outbox.CommitSourceBatchAsync(_serverId, null, next, [], ct).ConfigureAwait(false);
        }
        else
        {
            await _outbox.RecordGapAndResetCursorAsync(_serverId, previous, next, PlaytimeGapReason.Restart, ct).ConfigureAwait(false);
            _logger?.LogWarning("Playtime source coverage gap for server {ServerId}: {Reason}", _serverId, PlaytimeGapReason.Restart);
        }

        _cursor = next;
        _boundary = next.Offset;
        _boundaryUncertain = next.Offset != 0;
        _continuityVersion = tailer.ContinuityVersion;
    }

    public async Task ObservePollAsync(ILogTailer tailer, CancellationToken ct)
    {
        var cursor = _cursor ?? throw new InvalidOperationException("The playtime source has not been initialized.");
        if (tailer.ContinuityVersion != _continuityVersion)
        {
            _waitingForEnd ??= PlaytimeGapReason.ContinuityLost;
        }

        if (_waitingForEnd is { } reason)
        {
            if (!tailer.LastPollReachedEnd)
            {
                return;
            }

            var next = new PlaytimeSourceCursor(Guid.NewGuid(),
                tailer.CurrentFilePath ?? throw new InvalidOperationException("The log tailer has no file path."),
                tailer.CurrentOffset);
            await _outbox.RecordGapAndResetCursorAsync(_serverId, cursor, next, reason, ct).ConfigureAwait(false);
            _logger?.LogWarning("Playtime source coverage gap for server {ServerId}: {Reason}", _serverId, reason);
            _cursor = next;
            _boundary = next.Offset;
            _boundaryUncertain = next.Offset != 0;
            _continuityVersion = tailer.ContinuityVersion;
            _waitingForEnd = null;
            _parser.Reset();
            _players.Clear();
            _mapReentries.Clear();
            return;
        }

        var batchStart = cursor;
        var observations = new List<PlaytimeObservation>();
        foreach (var record in tailer.LastPollRecords)
        {
            if (record.EndOffset <= _boundary)
            {
                if (record.EndOffset == _boundary)
                {
                    _boundaryUncertain = false;
                }
                continue;
            }
            if (record.StartOffset < _boundary || record.StartOffset == _boundary && _boundaryUncertain)
            {
                _boundaryUncertain = false;
                continue;
            }

            var observation = ParseRecord(record.Text);
            if (observation is null)
            {
                continue;
            }

            observations.Add(observation);
            if (observations.Count == PlaytimeOutboxLimits.MaximumPendingEvents)
            {
                var next = batchStart with { Offset = record.EndOffset };
                _pending.Enqueue(new SourceBatch(batchStart, next, observations.ToArray()));
                batchStart = next;
                observations.Clear();
            }
        }

        if (tailer.CurrentOffset > batchStart.Offset)
        {
            var next = batchStart with { Offset = tailer.CurrentOffset };
            _pending.Enqueue(new SourceBatch(batchStart, next, observations.ToArray()));
        }

        await FlushPendingAsync(ct).ConfigureAwait(false);
    }

    public async Task FlushPendingAsync(CancellationToken ct)
    {
        while (_pending.TryPeek(out var batch))
        {
            try
            {
                await _outbox.CommitSourceBatchAsync(_serverId, batch.Expected, batch.Next, batch.Observations, ct).ConfigureAwait(false);
            }
            catch (PlaytimeOutboxCapacityExceededException)
            {
                await _dispatch(ct).ConfigureAwait(false);
                await _outbox.CommitSourceBatchAsync(_serverId, batch.Expected, batch.Next, batch.Observations, ct).ConfigureAwait(false);
            }
            _cursor = batch.Next;
            _pending.Dequeue();
        }
    }

    public Task ObservePollFailureAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _waitingForEnd ??= PlaytimeGapReason.PollFailure;
        return Task.CompletedTask;
    }

    public void MarkSkippedPoll() => _waitingForEnd ??= PlaytimeGapReason.Backlog;

    public void ObserveRoster(ServerRosterSnapshot roster, PlaytimeCompleteness completeness)
    {
        if (completeness is not (PlaytimeCompleteness.Complete or PlaytimeCompleteness.Partial))
        {
            return;
        }

        if (completeness == PlaytimeCompleteness.Complete)
        {
            var present = roster.Players.Select(player => player.SlotId).ToHashSet();
            foreach (var slot in _players.Keys.Where(slot => !present.Contains(slot)).ToArray())
            {
                _players.Remove(slot);
                _mapReentries.Remove(slot);
                _parser.RemovePlayer(slot);
            }
        }

        foreach (var player in roster.Players)
        {
            if (!string.IsNullOrWhiteSpace(player.PlayerGuid))
            {
                if (_players.TryGetValue(player.SlotId, out var previous) &&
                    !string.Equals(previous, player.PlayerGuid, StringComparison.OrdinalIgnoreCase))
                {
                    _mapReentries.Remove(player.SlotId);
                    _parser.RemovePlayer(player.SlotId);
                }
                _players[player.SlotId] = player.PlayerGuid;
            }
        }
    }

    private PlaytimeObservation? ParseRecord(string line)
    {
        var joinTeam = JoinTeamPattern.Match(line);
        if (joinTeam.Success && int.TryParse(joinTeam.Groups["slot"].Value, out var teamSlot) &&
            _parser.ConnectedPlayers.TryGetValue(teamSlot, out var existing) &&
            !string.Equals(existing.Guid, joinTeam.Groups["guid"].Value, StringComparison.OrdinalIgnoreCase))
        {
            _parser.RemovePlayer(teamSlot);
        }
        var quit = QuitPattern.Match(line);
        PlayerInfo? savedPlayer = null;
        if (quit.Success && int.TryParse(quit.Groups["slot"].Value, out var quitSlot))
        {
            _parser.ConnectedPlayers.TryGetValue(quitSlot, out savedPlayer);
        }
        var gameEvent = _parser.ParseLine(line);
        var now = DateTimeOffset.UtcNow;
        PlaytimeEventKind kind;
        string? guid = null;
        string? username = null;
        int? slot = null;
        string? map = null;
        switch (gameEvent)
        {
            case MapChangeEvent changed:
                _mapReentries.Clear();
                _mapReentries.UnionWith(_players.Keys);
                kind = PlaytimeEventKind.Map;
                map = changed.MapName;
                break;
            case PlayerConnectedEvent joined:
                guid = joined.PlayerGuid;
                username = joined.Username;
                slot = joined.SlotId;
                var same = _players.TryGetValue(joined.SlotId, out var previous) &&
                    string.Equals(previous, guid, StringComparison.OrdinalIgnoreCase);
                var isMapReentry = _mapReentries.Remove(joined.SlotId) && same;
                _players[joined.SlotId] = guid;
                if (isMapReentry || joinTeam.Success && same)
                {
                    return null;
                }
                kind = PlaytimeEventKind.Join;
                break;
            case PlayerDisconnectedEvent left:
                guid = left.PlayerGuid;
                username = left.Username;
                slot = left.SlotId;
                if (_players.TryGetValue(left.SlotId, out var current) &&
                    !string.Equals(current, guid, StringComparison.OrdinalIgnoreCase))
                {
                    if (savedPlayer is not null)
                    {
                        _parser.SetPlayer(left.SlotId, savedPlayer);
                    }
                }
                else
                {
                    _players.Remove(left.SlotId);
                    _mapReentries.Remove(left.SlotId);
                }
                kind = PlaytimeEventKind.Leave;
                break;
            default:
                return null;
        }

        var observation = new PlaytimeObservation
        {
            EventId = Guid.NewGuid(),
            ServerId = _serverId,
            GameType = _gameType,
            EventGeneratedUtc = now,
            EventPublishedUtc = null,
            EventUtc = now,
            SequenceId = _nextSequence(),
            Kind = kind,
            TimestampQuality = PlaytimeTimestampQuality.ParseTime,
            Freshness = PlaytimeFreshness.Unknown,
            Completeness = PlaytimeCompleteness.NotApplicable,
            GameIdentifier = guid,
            Username = username,
            Slot = slot,
            MapName = map
        };
        try
        {
            observation.Validate();
            return observation;
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Invalid playtime log observation for server {ServerId}", _serverId);
            return null;
        }
    }

    private sealed record SourceBatch(PlaytimeSourceCursor Expected, PlaytimeSourceCursor Next,
        IReadOnlyList<PlaytimeObservation> Observations);
}
