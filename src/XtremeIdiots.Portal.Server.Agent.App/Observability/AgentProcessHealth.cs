using System.Collections.Concurrent;
using MX.Platform.Monitoring;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Observability;

internal sealed record BanCheckHealth(DateTimeOffset CheckedAt, bool Success,
    string? PendingETag, DateTimeOffset? PendingDeliverySince, DateTimeOffset? PendingImportSince);

public sealed class AgentProcessHealth
{
    internal ConcurrentDictionary<Guid, DateTimeOffset> Heartbeats { get; } = new();
    internal ConcurrentDictionary<Guid, BanCheckHealth> BanChecks { get; } = new();
    private readonly Dictionary<(Guid Server, bool Bans), DateTimeOffset> firstExpected = [];

    internal (string Status, int Expected, int Healthy, int Failed, int Unknown) Evaluate(
        AgentInventory? inventory, bool bans, DateTimeOffset now)
    {
        if (inventory is null || now - inventory.ObservedAt > ProcessHealthTelemetry.Age("freshnessSeconds"))
        {
            return ("unknown", 0, 0, 0, 0);
        }

        var expected = inventory.Servers.Where(server => bans ? server.BanFileSyncEnabled : server.AgentEnabled).ToArray();
        var all = inventory.Servers.Select(server => server.ServerId).ToHashSet();
        foreach (var id in firstExpected.Keys.Where(id => !all.Contains(id.Server)
            || (id.Bans == bans && !expected.Any(server => server.ServerId == id.Server))).ToArray())
        {
            firstExpected.Remove(id);
            if (id.Bans)
            {
                BanChecks.TryRemove(id.Server, out _);
            }
            else
            {
                Heartbeats.TryRemove(id.Server, out _);
            }
        }
        var healthy = 0;
        var failed = 0;
        var unknown = 0;
        foreach (var server in expected)
        {
            var key = (server.ServerId, bans);
            firstExpected.TryAdd(key, now);
            if (bans && !server.AgentEnabled)
            {
                failed++;
                continue;
            }
            if (!bans)
            {
                if (Heartbeats.TryGetValue(server.ServerId, out var at)
                    && now - at <= ProcessHealthTelemetry.Age("agentHeartbeatSeconds"))
                {
                    healthy++;
                }
                else if (now - firstExpected[key] > ProcessHealthTelemetry.Age("agentHeartbeatSeconds"))
                {
                    failed++;
                }
                else
                {
                    unknown++;
                }
            }
            else if (BanChecks.TryGetValue(server.ServerId, out var check))
            {
                if (!check.Success || now - check.CheckedAt > ProcessHealthTelemetry.Age("banCheckSeconds")
                    || (check.PendingDeliverySince is { } pending && now - pending > ProcessHealthTelemetry.Age("banDeliverySeconds"))
                    || (check.PendingImportSince is { } imported && now - imported > ProcessHealthTelemetry.Age("banImportSeconds")))
                {
                    failed++;
                }
                else
                {
                    healthy++;
                }
            }
            else if (now - firstExpected[key] > ProcessHealthTelemetry.Age("banCheckSeconds"))
            {
                failed++;
            }
            else
            {
                unknown++;
            }
        }
        return (ProcessHealthTelemetry.Classify(expected.Length, healthy, failed, unknown),
            expected.Length, healthy, failed, unknown);
    }

    internal void Publish(ProcessHealthTelemetry telemetry, AgentInventory? inventory, DateTimeOffset now)
    {
        foreach (var bans in new[] { false, true })
        {
            var (Status, Expected, Healthy, Failed, Unknown) = Evaluate(inventory, bans, now);
            telemetry.Publish(bans ? "xi.process.ban-sync.delivery" : "xi.process.server-agent.fleet",
                Status, now, Expected, Healthy, Failed, Unknown);
        }
    }
}
