using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using MX.Observability.ApplicationInsights.Filtering.Configuration;

namespace MX.Platform.Monitoring;

// Mirrored from platform-monitoring/contract; policy changes belong to that repository.
public sealed class ProcessHealthTelemetry(TelemetryClient client)
{
    public static JsonElement Policy { get; } = LoadPolicy();
    public static TimeSpan Interval => TimeSpan.FromSeconds(Policy.GetProperty("intervalSeconds").GetInt32());
    public static TimeSpan Age(string name) => TimeSpan.FromSeconds(Policy.GetProperty(name).GetInt32());

    public static string Classify(int expected, int healthy, int failed, int unknown, bool completeFailureConfirmed = false)
    {
        if (expected < 0 || healthy < 0 || failed < 0 || unknown < 0 || (long)healthy + failed + unknown != expected)
        {
            throw new ArgumentException("Process counts must account for the complete expected inventory.");
        }
        if (expected == 0)
        {
            return "unknown";
        }
        if (failed == expected && completeFailureConfirmed)
        {
            return "outage";
        }
        if (failed > 0)
        {
            return "degraded";
        }
        return unknown > 0 ? "unknown" : "operational";
    }

    public void Publish(string checkId, string status, DateTimeOffset observedAt, int expected, int healthy, int failed, int unknown)
    {
        var binding = Policy.GetProperty("bindings").EnumerateArray()
            .Single(item => item.GetProperty("checkId").GetString() == checkId);
        if (status != Classify(expected, healthy, failed, unknown, status == "outage"))
        {
            throw new ArgumentException("Invalid process health status.", nameof(status));
        }
        var telemetry = new EventTelemetry(Policy.GetProperty("eventName").GetString())
        {
            Timestamp = observedAt
        };
        foreach (var key in new[] { "siteId", "componentId", "checkId" })
        {
            telemetry.Properties[key] = binding.GetProperty(key).GetString()
                ?? throw new InvalidDataException($"Missing process binding field {key}.");
        }
        telemetry.Properties["contractVersion"] = Policy.GetProperty("version").GetInt32().ToString(CultureInfo.InvariantCulture);
        telemetry.Properties["status"] = status;
        telemetry.Properties["expected"] = expected.ToString(CultureInfo.InvariantCulture);
        telemetry.Properties["healthy"] = healthy.ToString(CultureInfo.InvariantCulture);
        telemetry.Properties["failed"] = failed.ToString(CultureInfo.InvariantCulture);
        telemetry.Properties["unknown"] = unknown.ToString(CultureInfo.InvariantCulture);
        telemetry.Properties["observedAt"] = observedAt.ToString("O", CultureInfo.InvariantCulture);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(telemetry.Properties)) > Policy.GetProperty("maximumRecordBytes").GetInt32())
        {
            throw new InvalidOperationException("Process telemetry exceeded its reviewed record-size budget.");
        }
        client.TrackEvent(telemetry);
    }

    public static void ConfigureFiltering(TelemetryFilterOptions options)
    {
        var events = options.CustomEvents;
        var names = events.AllowedNames?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var prefixes = events.AllowedNamePrefixes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (!events.Enabled || (names.Length == 0 && prefixes.Length == 0))
        {
            return;
        }
        events.AllowedNames = string.Join(',', names.Append("ProcessHealthSnapshot").Append("ProcessJobSnapshot")
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static JsonElement LoadPolicy()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "process-health-policy.json")));
        if (document.RootElement.GetProperty("version").GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported strategic process-health policy version.");
        }
        return document.RootElement.Clone();
    }
}
