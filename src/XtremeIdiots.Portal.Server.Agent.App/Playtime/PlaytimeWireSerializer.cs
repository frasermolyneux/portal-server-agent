using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Shared = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

internal static class PlaytimeWireSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                type =>
                {
                    if (type.Type == typeof(Shared.PlaytimeRosterPlayer))
                    {
                        type.Properties.Single(property => property.Name == "isBot").ShouldSerialize = (_, _) => true;
                    }
                }
            }
        }
    };

    public static string Serialize(PlaytimeObservation observation)
    {
        observation.Validate();
        if (observation.EventPublishedUtc is not { } publishedUtc)
        {
            throw new InvalidOperationException("The publication timestamp must be persisted before sending.");
        }
        return JsonSerializer.Serialize(new Shared.PlaytimeObservationEvent
        {
            EventGeneratedUtc = observation.EventGeneratedUtc.UtcDateTime,
            EventPublishedUtc = publishedUtc.UtcDateTime,
            ServerId = observation.ServerId,
            GameType = observation.GameType,
            SequenceId = observation.SequenceId,
            Kind = observation.Kind switch
            {
                PlaytimeEventKind.Join => Shared.PlaytimeObservationKind.PlayerJoined,
                PlaytimeEventKind.Leave => Shared.PlaytimeObservationKind.PlayerLeft,
                PlaytimeEventKind.Map => Shared.PlaytimeObservationKind.MapChanged,
                PlaytimeEventKind.Roster => Shared.PlaytimeObservationKind.ServerRoster,
                _ => throw new ArgumentOutOfRangeException(nameof(observation))
            },
            Playtime = new Shared.PlaytimeEventMetadata
            {
                ObservationId = observation.EventId,
                EventUtc = observation.EventUtc.UtcDateTime,
                TimestampQuality = observation.TimestampQuality switch
                {
                    PlaytimeTimestampQuality.ParsedLogTimestamp => Shared.PlaytimeTimestampQuality.ParsedLogTimestamp,
                    PlaytimeTimestampQuality.ProducerTimestamp => Shared.PlaytimeTimestampQuality.ProducerTimestamp,
                    PlaytimeTimestampQuality.RosterSnapshotTimestamp => Shared.PlaytimeTimestampQuality.RosterSnapshotTimestamp,
                    PlaytimeTimestampQuality.ParseTime => Shared.PlaytimeTimestampQuality.ParseTime,
                    _ => throw new ArgumentOutOfRangeException(nameof(observation))
                },
                Freshness = observation.Freshness switch
                {
                    PlaytimeFreshness.Unknown => Shared.PlaytimeObservationFreshness.Unknown,
                    PlaytimeFreshness.Live => Shared.PlaytimeObservationFreshness.Live,
                    _ => throw new ArgumentOutOfRangeException(nameof(observation))
                },
                ProducerEpoch = observation.Epoch,
                ProducerSequence = observation.ProducerSequence,
                ConnectionCorrelationId = observation.CorrelationId,
                RosterCompleteness = observation.Completeness switch
                {
                    PlaytimeCompleteness.NotApplicable => Shared.PlaytimeRosterCompleteness.NotApplicable,
                    PlaytimeCompleteness.Complete => Shared.PlaytimeRosterCompleteness.Complete,
                    PlaytimeCompleteness.Partial => Shared.PlaytimeRosterCompleteness.Partial,
                    PlaytimeCompleteness.Failed => Shared.PlaytimeRosterCompleteness.Failed,
                    _ => throw new ArgumentOutOfRangeException(nameof(observation))
                }
            },
            PlayerGuid = observation.GameIdentifier,
            Username = observation.Username,
            SteamId = observation.SteamId,
            SlotId = observation.Slot,
            IsBot = observation.IsBot,
            MapName = observation.MapName,
            Players = observation.Players.IsDefault ? null : observation.Players.Select(player =>
                new Shared.PlaytimeRosterPlayer
                {
                    PlayerGuid = player.GameIdentifier,
                    Username = player.Username,
                    SlotId = player.Slot,
                    IsBot = player.IsBot
                }).ToArray(),
            PlaytimeCoverage = observation.Coverage is null ? null : new Shared.PlaytimeCoverage
            {
                ProducerStartedUtc = observation.Coverage.ProducerStartedUtc.UtcDateTime,
                DroppedCapacity = observation.Coverage.DroppedCapacity,
                DroppedAttempts = observation.Coverage.DroppedAttempts,
                DroppedAge = observation.Coverage.DroppedAge
            }
        }, Options);
    }
}
