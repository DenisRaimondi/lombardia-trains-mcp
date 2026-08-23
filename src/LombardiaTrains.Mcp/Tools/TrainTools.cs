using System.ComponentModel;
using System.Globalization;
using System.Text;
using LombardiaTrains.Mcp.Clients;
using LombardiaTrains.Mcp.Models;
using LombardiaTrains.Mcp.Services;
using ModelContextProtocol.Server;

namespace LombardiaTrains.Mcp.Tools;

/// <summary>
/// The tools exposed over MCP. They return preformatted text rather than raw
/// JSON: the caller is a language model, and a compact table costs far fewer
/// tokens than the original payload while keeping every field that matters.
///
/// Two decisions are deliberate and worth stating.
///
/// A station name is never resolved by silently taking the first match.
/// "Milano" alone is twenty-six stations, and many towns have both a national
/// station and a separate "Nord" one served by different trains — answering
/// about the wrong one while sounding certain is worse than asking which was
/// meant.
///
/// And when a place is not covered, the answer says so and says where the
/// coverage ends, so the model can decline instead of inventing a train.
/// </summary>
[McpServerToolType]
public sealed class TrainTools(
    ViaggiaTrenoClient viaggiaTreno,
    TrenordClient trenord,
    ConnectionFinder connections)
{
    private const string Coverage =
        "This service covers the Italian rail network: RFI/Trenitalia plus Trenord and FNM. " +
        "Cross-border trains appear as far as the frontier (Stabio, Chiasso, Domodossola), " +
        "but stations abroad — Lugano, Zurich, Nice — are not in it.";

    // ------------------------------------------------------------------ time

    [McpServerTool(Name = "now")]
    [Description("Current date and time in Italy, with the day of the week. Call this before " +
                 "answering anything relative such as 'today', 'tonight', 'Saturday' or 'in an " +
                 "hour': the other tools need an explicit date and cannot guess one.")]
    public string Now()
    {
        var rome = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ViaggiaTrenoClient.RomeTz);
        return string.Create(CultureInfo.InvariantCulture,
            $"{rome:dddd, d MMMM yyyy, HH:mm} (Europe/Rome, UTC{rome.Offset.Hours:+00;-00}:00)\nISO: {rome:yyyy-MM-dd'T'HH:mm}");
    }

    // --------------------------------------------------------------- station

    [McpServerTool(Name = "search_station")]
    [Description("Find railway stations by name and return their codes. Names are frequently " +
                 "ambiguous — 'Milano' matches twenty-six stations, and many towns have both a " +
                 "national station and a separate Nord one served by different trains — so check " +
                 "here first when the user gave a name rather than a code.")]
    public async Task<string> SearchStationAsync(
        [Description("Full or partial station name, for example 'milano centrale' or 'como'.")]
        string name,
        CancellationToken ct = default)
    {
        var stations = await viaggiaTreno.SearchStationAsync(name, ct);
        if (stations.Count == 0)
            return $"No station matches '{name}'.\n{Coverage}";

        var sb = new StringBuilder($"{stations.Count} station(s) matching '{name}':\n");
        foreach (var s in stations.Take(25))
            sb.AppendLine($"  {s.Id,-8} {s.LongName}");
        if (stations.Count > 25) sb.AppendLine($"  ... and {stations.Count - 25} more");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- boards

    [McpServerTool(Name = "get_departures")]
    [Description("Departure board for a station: time, train, destination, delay in minutes " +
                 "(negative means early) and platform ('-' when not yet assigned). Works for " +
                 "future dates as well as today.")]
    public Task<string> GetDeparturesAsync(
        [Description("Station code such as 'S01136', or a name such as 'milano centrale'.")]
        string station,
        [Description("When, as 'HH:mm' for today or ISO '2026-08-29T09:00' for another day. " +
                     "Defaults to now. Call the 'now' tool first if the user said something " +
                     "relative like 'Saturday'.")]
        string? at = null,
        [Description("How many rows to return. Default 10.")]
        int limit = 10,
        CancellationToken ct = default) =>
        BoardAsync(station, at, limit, departures: true, ct);

    [McpServerTool(Name = "get_arrivals")]
    [Description("Arrival board for a station: time, train, origin, delay and platform.")]
    public Task<string> GetArrivalsAsync(
        [Description("Station code such as 'S01136', or a name such as 'milano centrale'.")]
        string station,
        [Description("When, as 'HH:mm' for today or ISO '2026-08-29T09:00' for another day.")]
        string? at = null,
        [Description("How many rows to return. Default 10.")]
        int limit = 10,
        CancellationToken ct = default) =>
        BoardAsync(station, at, limit, departures: false, ct);

    // ----------------------------------------------------------- connections

    [McpServerTool(Name = "find_connection")]
    [Description("Direct trains between two stations, with departure, arrival, duration, delay " +
                 "and platform. Only direct services: this reads live boards rather than a " +
                 "timetable, so journeys needing a change are not found and the answer says so.")]
    public async Task<string> FindConnectionAsync(
        [Description("Origin station code or name.")] string from,
        [Description("Destination station code or name.")] string to,
        [Description("When to leave, as 'HH:mm' for today or ISO '2026-08-29T09:00'. Defaults to now.")]
        string? at = null,
        [Description("How many connections to return. Default 5.")]
        int limit = 5,
        CancellationToken ct = default)
    {
        var origin = await ResolveAsync(from, ct);
        if (origin.Message is not null) return origin.Message;

        var destination = await ResolveAsync(to, ct);
        if (destination.Message is not null) return destination.Message;

        var when = ParseWhen(at);
        var found = await connections.FindAsync(
            origin.Code!, origin.Name!, destination.Name!, when, Math.Clamp(limit, 1, 10), ct);

        if (found.Count == 0)
            return $"No direct train from {origin.Name} to {destination.Name} around {Stamp(when)}.\n" +
                   "There may still be a route with a change, which this tool cannot see: it reads " +
                   "live departure boards, not a timetable planner.";

        var sb = new StringBuilder(
            $"Direct trains {origin.Name} -> {destination.Name}, from {Stamp(when)}\n");
        foreach (var c in found)
        {
            var duration = c.DurationMinutes is null ? "" : $"  {c.DurationMinutes}min";
            var stops = c.Stops == 1 ? "direct" : $"{c.Stops} stops";
            var cancelled = c.Cancelled ? "  [CANCELLED]" : "";
            sb.AppendLine(
                $"  {c.DepartureTime} -> {c.ArrivalTime}{duration,-8} {c.Train,-10} " +
                $"{FormatDelay(c.Delay),5}  platform {c.Platform ?? "-"}  ({stops}){cancelled}");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- trains

    [McpServerTool(Name = "get_train")]
    [Description("Live status of a single train, stop by stop: delay, platforms, cancellations " +
                 "and crowding where published. Asks Trenord first and falls back to " +
                 "ViaggiaTreno, because neither source covers every line on its own.")]
    public async Task<string> GetTrainAsync(
        [Description("Train number, for example '4307' or '11866'.")] string trainNumber,
        CancellationToken ct = default)
    {
        var runs = await trenord.GetTrainAsync(trainNumber, ct);
        if (runs.Count > 0) return FormatTrenord(runs);

        var progress = await viaggiaTreno.GetTrainProgressAsync(trainNumber, ct);
        if (progress is not null) return FormatViaggiaTreno(progress);

        return $"Train {trainNumber} was not found on either source. It may not be running today, " +
               $"or the number may belong to a line neither API covers.\n{Coverage}";
    }

    // ------------------------------------------------------------- internals

    private async Task<string> BoardAsync(
        string station, string? at, int limit, bool departures, CancellationToken ct)
    {
        var resolved = await ResolveAsync(station, ct);
        if (resolved.Message is not null) return resolved.Message;

        var when = ParseWhen(at);
        var rows = departures
            ? await viaggiaTreno.GetDeparturesAsync(resolved.Code!, when, ct)
            : await viaggiaTreno.GetArrivalsAsync(resolved.Code!, when, ct);

        var kind = departures ? "Departures" : "Arrivals";
        if (rows.Count == 0)
            return $"{kind} — {resolved.Name} ({resolved.Code}) around {Stamp(when)}: " +
                   "no trains in this window.";

        var sb = new StringBuilder(
            $"{kind} — {resolved.Name} ({resolved.Code}), {Stamp(when)}\n");
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

    private readonly record struct Resolution(string? Code, string? Name, string? Message);

    /// <summary>
    /// Turns user input into a single station, or into a message explaining why
    /// it could not. Codes pass through untouched; names are looked up, and an
    /// ambiguous name is reported rather than guessed at.
    /// </summary>
    private async Task<Resolution> ResolveAsync(string input, CancellationToken ct)
    {
        var trimmed = input.Trim();

        if (trimmed.Length > 1 && trimmed[0] is 'S' or 's' && trimmed[1..].All(char.IsDigit))
        {
            var code = trimmed.ToUpperInvariant();
            // Resolve the code to its real name: without it nothing downstream
            // can match this station inside a train's stop list.
            var resolvedName = await viaggiaTreno.GetStationNameAsync(code, ct);
            return new Resolution(code, resolvedName ?? code, null);
        }

        var matches = await viaggiaTreno.SearchStationAsync(trimmed, ct);

        if (matches.Count == 0)
            return new Resolution(null, null, $"No station matches '{trimmed}'.\n{Coverage}");

        // An exact name match settles it even when the search returns siblings.
        var exact = matches.Where(m =>
            string.Equals(m.LongName?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.ShortName?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)).ToList();

        var chosen = exact.Count == 1 ? exact[0] : (matches.Count == 1 ? matches[0] : null);

        if (chosen is not null)
            return new Resolution(chosen.Id, chosen.ShortName ?? chosen.LongName ?? trimmed, null);

        var sb = new StringBuilder(
            $"'{trimmed}' matches {matches.Count} stations, which serve different trains. " +
            "Ask which one is meant, or pass the code:\n");
        foreach (var m in matches.Take(15))
            sb.AppendLine($"  {m.Id,-8} {m.LongName}");
        if (matches.Count > 15) sb.AppendLine($"  ... and {matches.Count - 15} more");

        return new Resolution(null, null, sb.ToString());
    }

    /// <summary>
    /// Accepts "HH:mm" for today and ISO 8601 for any other day, so that a
    /// question about Saturday can actually be asked. Anything unparseable
    /// falls back to now rather than failing the call.
    /// </summary>
    internal static DateTimeOffset ParseWhen(string? at)
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ViaggiaTrenoClient.RomeTz);
        if (string.IsNullOrWhiteSpace(at)) return now;

        var text = at.Trim();

        if (TimeSpan.TryParseExact(text, [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var tod))
            return new DateTimeOffset(now.Date.Add(tod), now.Offset);

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            var offset = ViaggiaTrenoClient.RomeTz.GetUtcOffset(parsed);
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), offset);
        }

        return now;
    }

    // ----------------------------------------------------------- formatting

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
            // to report, so each of these is a null check, not a value check.
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
                var cancelled = stop.Cancelled == true ? "  [CANCELLED]" : "";

                sb.AppendLine(
                    $"  {Trim(stop.Station?.Name, 26),-26} {Hhmm(stop.DepTime ?? stop.ArrTime)}  " +
                    $"actual {Hhmm(real),-5} {FormatDelay(delay),5}  " +
                    $"platform {stop.Platform ?? "-"}{cancelled}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatViaggiaTreno(VtTrainProgress p)
    {
        var sb = new StringBuilder($"{p.Category} {p.TrainNumber} — {p.Origin} -> {p.Destination}\n");
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

    /// <summary>
    /// Dates are always rendered in the invariant culture. Formatting them with
    /// the machine culture makes the server answer in Italian on one host and
    /// in English on another, which is the same trap ViaggiaTreno sets with its
    /// timestamps.
    /// </summary>
    private static string Stamp(DateTimeOffset when) =>
        when.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

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
