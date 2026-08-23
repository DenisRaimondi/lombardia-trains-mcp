using System.ComponentModel;
using System.Globalization;
using System.Text;
using LombardiaTrains.Mcp.Clients;
using LombardiaTrains.Mcp.Models;
using ModelContextProtocol.Server;

namespace LombardiaTrains.Mcp.Tools;

/// <summary>
/// The tools exposed over MCP. They return preformatted text rather than raw
/// JSON: the caller is a language model, and a compact table costs far fewer
/// tokens than the original payload while keeping every field that matters.
/// </summary>
[McpServerToolType]
public sealed class TrainTools(ViaggiaTrenoClient viaggiaTreno, TrenordClient trenord)
{
    [McpServerTool(Name = "search_station")]
    [Description("Find Italian railway stations by name and return their ViaggiaTreno codes. " +
                 "Use this first when you only know a station name: the other tools need the code.")]
    public async Task<string> SearchStationAsync(
        [Description("Full or partial station name, for example 'castellanza' or 'milano cadorna'.")]
        string name,
        CancellationToken ct = default)
    {
        var stations = await viaggiaTreno.SearchStationAsync(name, ct);
        if (stations.Count == 0)
            return $"No station matches '{name}'.";

        var sb = new StringBuilder($"Stations matching '{name}':\n");
        foreach (var s in stations.Take(20))
            sb.AppendLine($"  {s.Id,-8} {s.LongName}");
        return sb.ToString();
    }

    [McpServerTool(Name = "get_departures")]
    [Description("Departure board for a station: time, train, destination, delay and platform. " +
                 "Accepts either a station code (S01136) or a station name.")]
    public Task<string> GetDeparturesAsync(
        [Description("Station code such as 'S01136', or a station name such as 'castellanza'.")]
        string station,
        [Description("Optional time of day as 'HH:mm'. Defaults to now.")]
        string? at = null,
        [Description("How many rows to return. Default 10.")]
        int limit = 10,
        CancellationToken ct = default) =>
        BoardAsync(station, at, limit, departures: true, ct);

    [McpServerTool(Name = "get_arrivals")]
    [Description("Arrival board for a station: time, train, origin, delay and platform.")]
    public Task<string> GetArrivalsAsync(
        [Description("Station code such as 'S01136', or a station name such as 'castellanza'.")]
        string station,
        [Description("Optional time of day as 'HH:mm'. Defaults to now.")]
        string? at = null,
        [Description("How many rows to return. Default 10.")]
        int limit = 10,
        CancellationToken ct = default) =>
        BoardAsync(station, at, limit, departures: false, ct);

    [McpServerTool(Name = "get_train")]
    [Description("Live status of a single train, stop by stop: delay, platforms, cancellations, " +
                 "and crowding when the operator publishes it. Queries Trenord first, then falls " +
                 "back to ViaggiaTreno, because neither source covers every line on its own.")]
    public async Task<string> GetTrainAsync(
        [Description("Train number, for example '4307' or '11866'.")]
        string trainNumber,
        CancellationToken ct = default)
    {
        var runs = await trenord.GetTrainAsync(trainNumber, ct);
        if (runs.Count > 0)
            return FormatTrenord(runs);

        var progress = await viaggiaTreno.GetTrainProgressAsync(trainNumber, ct);
        if (progress is not null)
            return FormatViaggiaTreno(progress);

        return $"Train {trainNumber} was not found on either source. " +
               "It may not be running today, or the number may belong to a line neither API covers.";
    }

    // ------------------------------------------------------------------ boards

    private async Task<string> BoardAsync(
        string station, string? at, int limit, bool departures, CancellationToken ct)
    {
        var (code, label) = await ResolveStationAsync(station, ct);
        if (code is null)
            return $"No station matches '{station}'.";

        var when = ParseTime(at);
        var rows = departures
            ? await viaggiaTreno.GetDeparturesAsync(code, when, ct)
            : await viaggiaTreno.GetArrivalsAsync(code, when, ct);

        var kind = departures ? "Departures" : "Arrivals";
        if (rows.Count == 0)
            return $"{kind} from {label} ({code}) at {when:HH:mm}: no trains in this window.";

        var sb = new StringBuilder($"{kind} — {label} ({code}), {when:HH:mm}\n");
        foreach (var r in rows.Take(Math.Clamp(limit, 1, 50)))
        {
            var time = (departures ? r.DepartureTime : r.ArrivalTime) ?? "--:--";
            var who = (departures ? r.Destination : r.Origin) ?? "?";
            var flag = r.IsCancelled ? "  [CANCELLED]" : "";
            sb.AppendLine(
                $"  {time}  {r.Category}{r.TrainNumber,-6} {Trim(who, 26),-26} " +
                $"{FormatDelay(r.Delay),5}  platform {r.Platform}{flag}");
        }
        return sb.ToString();
    }

    private async Task<(string? Code, string Label)> ResolveStationAsync(string input, CancellationToken ct)
    {
        var trimmed = input.Trim();
        if (trimmed.Length > 1 && trimmed[0] is 'S' or 's' && trimmed[1..].All(char.IsDigit))
            return (trimmed.ToUpperInvariant(), trimmed.ToUpperInvariant());

        var found = await viaggiaTreno.SearchStationAsync(trimmed, ct);
        var first = found.FirstOrDefault();
        return first is null ? (null, trimmed) : (first.Id, first.ShortName ?? first.LongName ?? trimmed);
    }

    // ----------------------------------------------------------------- Trenord

    private static string FormatTrenord(IReadOnlyList<TrenordRun> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            var journey = run.Journeys.FirstOrDefault();
            var train = journey?.Train;
            if (train is null) continue;

            sb.AppendLine(
                $"{train.Line} {train.TrainId} — {run.DepStation?.Name} {Hhmm(run.DepTime)} " +
                $"-> {run.ArrStation?.Name} {Hhmm(run.ArrTime)}");
            sb.AppendLine(
                $"  status: {train.StatusText} | delay: {FormatDelay(train.Delay)} | " +
                $"operator: {train.Operator} | bicycles: {(train.Bicycle == true ? "yes" : "no")}");

            if (!string.IsNullOrWhiteSpace(train.ActualStation))
                sb.AppendLine($"  last seen: {train.ActualStation} {Hhmm(train.ActualTime)}");

            // Conditional fields: absent from the payload when there is nothing
            // to report, so every one of these is a null check, not a value check.
            if (train.CrowdingLabel is not null || train.AverageCrowding is not null)
                sb.AppendLine($"  crowding: {train.CrowdingLabel ?? "?"} ({train.AverageCrowding}%)");

            if (train.SuppressionType is not null)
                sb.AppendLine($"  ** SUPPRESSED (type {train.SuppressionType}) **");

            foreach (var alert in train.Alerts ?? [])
                sb.AppendLine($"  ** ALERT [{alert.Type}]: {alert.Description ?? alert.Title} **");

            if (journey!.Stops.Count > 0) sb.AppendLine("  ---");

            foreach (var stop in journey.Stops)
            {
                var actual = stop.ActualData;
                var real = actual?.DepActualTime ?? actual?.ArrActualTime;
                var delay = actual?.DepDelay ?? actual?.ArrDelay;
                var planned = Hhmm(stop.DepTime ?? stop.ArrTime);
                var cancelled = stop.Cancelled == true ? "  [CANCELLED]" : "";

                sb.AppendLine(
                    $"  {Trim(stop.Station?.Name, 26),-26} {planned}  " +
                    $"actual {Hhmm(real),-5} {FormatDelay(delay),5}  " +
                    $"platform {stop.Platform ?? "-"}{cancelled}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ----------------------------------------------------------- ViaggiaTreno

    private static string FormatViaggiaTreno(VtTrainProgress p)
    {
        var sb = new StringBuilder(
            $"{p.Category} {p.TrainNumber} — {p.Origin} -> {p.Destination}\n");
        sb.AppendLine($"  delay: {FormatDelay(p.Delay)} | last seen: {p.LastSeenAt} {p.LastSeenTime}");
        if (p.IsCancelled) sb.AppendLine("  ** CANCELLED **");
        sb.AppendLine("  ---");

        foreach (var stop in p.Stops)
        {
            var planned = ViaggiaTrenoClient.ToRomeHhMm(
                stop.ScheduledDeparture ?? stop.ScheduledArrival ?? stop.Scheduled);
            var actual = ViaggiaTrenoClient.ToRomeHhMm(
                stop.ActualDeparture ?? stop.ActualArrival ?? stop.Actual);

            sb.AppendLine(
                $"  {Trim(stop.Station, 26),-26} {planned}  actual {actual}  " +
                $"{FormatDelay(stop.Delay),5}  platform {stop.Platform}");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- helpers

    private static DateTimeOffset ParseTime(string? at)
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ViaggiaTrenoClient.RomeTz);
        if (string.IsNullOrWhiteSpace(at)) return now;

        return TimeSpan.TryParse(at, CultureInfo.InvariantCulture, out var tod)
            ? new DateTimeOffset(now.Date.Add(tod), now.Offset)
            : now;
    }

    private static string FormatDelay(int? minutes) => minutes switch
    {
        null => "   ?",
        > 0 => $"+{minutes}'",
        < 0 => $"{minutes}'",
        _ => "  0'"
    };

    private static string Hhmm(string? time) =>
        string.IsNullOrWhiteSpace(time) ? "--:--" : time[..Math.Min(5, time.Length)];

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "?";
        return value.Length <= max ? value : value[..max];
    }
}
