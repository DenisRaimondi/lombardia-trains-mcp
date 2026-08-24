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
    SwissTransportClient swiss,
    ConnectionFinder connections,
    GtfsClient gtfs,
    GtfsPlanner planner,
    LiveCheck live)
{
    /// <summary>
    /// What to say when a journey cannot be planned. The tools that do work
    /// across the whole Italian network are named, because "outside coverage"
    /// on its own reads as "this server cannot help with these stations", and
    /// for live departures and delays it can.
    /// </summary>
    /// <summary>Below this, a change is not one a traveller would make.</summary>
    private const int MinimumChangeMinutes = 4;

    private const string OutsideThePlan =
        "Journey planning covers the Lombardy regional network and the lines across the Swiss " +
        "border. Elsewhere in Italy there is no timetable here to plan from — but the live data " +
        "still works everywhere: get_departures and get_arrivals for any station, find_connection " +
        "for a direct train between two of them, and get_train for the position and delay of any " +
        "train in the country.";

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

    [McpServerTool(Name = "find_journey")]
    [Description("Full journey between two places, changes included. Use this rather than " +
                 "find_connection whenever the two places are not obviously on the same line, or " +
                 "when a change may be needed. Plans from the Lombardy regional timetable, which " +
                 "reaches the Swiss stations on the cross-border lines, and falls back to Swiss " +
                 "open data for anywhere it does not cover. Returns timetabled times: pair it " +
                 "with get_departures or get_train for delays, platforms and cancellations.")]
    public async Task<string> FindJourneyAsync(
        [Description("Origin, as a place or station name: 'Milano Centrale', 'Malpensa', 'Como'.")]
        string from,
        [Description("Destination, as a place or station name. May be in Switzerland: 'Lugano', 'Chiasso'.")]
        string to,
        [Description("When to leave, as 'HH:mm' for today or ISO '2026-08-29T09:00'. Defaults to now.")]
        string? at = null,
        [Description("How many journeys to return. Default 3.")]
        int limit = 3,
        CancellationToken ct = default)
    {
        var when = ParseWhen(at);

        // The regional timetable is asked first. It is the only source here that
        // covers the Lombardy network completely, and its stop codes are the ones
        // the live tools already use, so an itinerary it returns can be followed
        // up train by train. It answers with null when it cannot help — a place
        // outside the region, or a day with nothing left — and the Swiss planner,
        // which reaches much further but knows Italy only near the border, takes
        // over from there.
        var regional = await PlanFromTimetableAsync(from, to, when, limit, ct);
        if (regional is not null) return regional;

        // Beyond the regional timetable there is only a planner built for
        // Switzerland, which reaches into Italy near the border and no further.
        // Asked about anywhere else it does not decline: it resolves the name to
        // the closest thing in its own index and answers about that instead.
        // Asked to plan Milano Centrale to Roma Termini it returned a confident
        // two-hour itinerary to a beauty clinic in Locarno.
        //
        // Whether a place is Italian is not the test — that index holds Zurich
        // and ViaggiaTreno holds Zurich Altstetten, so nationality separates
        // nothing here. What separates them is whether the answer is about the
        // places that were asked for.
        var journeys = await swiss.GetConnectionsAsync(from, to, when, Math.Clamp(limit, 1, 6), ct);

        if (journeys.Count == 0)
            return $"No journey found from '{from}' to '{to}' around {Stamp(when)}.\n" +
                   OutsideThePlan;

        if (!Resembles(from, journeys[0].From?.Name) || !Resembles(to, journeys[0].To?.Name))
            return $"Cannot plan {from} to {to}. The planner matched them to " +
                   $"'{journeys[0].From?.Name}' and '{journeys[0].To?.Name}', which is not what " +
                   $"was asked for, so its answer has been discarded rather than passed on.\n" +
                   OutsideThePlan;

        var sb = new StringBuilder($"{from} -> {to}, from {Stamp(when)}\n");

        foreach (var journey in journeys)
        {
            var changes = journey.Transfers switch
            {
                0 or null => "direct",
                1 => "1 change",
                var n => $"{n} changes"
            };
            var duration = journey.DurationMinutes is { } m ? $"{m / 60}h{m % 60:00}" : "?";

            sb.AppendLine(
                $"\n  {journey.From?.Departure:HH\\:mm} {journey.From?.Name} " +
                $"-> {journey.To?.Arrival:HH\\:mm} {journey.To?.Name}   {duration}, {changes}");

            // Walking and connecting legs are shown too. Hiding them leaves a
            // gap between the stated start and the first train, and the reader
            // has no way to tell whether it is a five-minute walk or an hour.
            foreach (var leg in journey.Sections)
                sb.AppendLine(
                    $"      {(leg.IsWalk ? "(transfer)" : leg.Journey!.Label),-12} " +
                    $"{leg.Departure?.Name} {leg.Departure?.Departure:HH\\:mm}" +
                    $" -> {leg.Arrival?.Name} {leg.Arrival?.Arrival:HH\\:mm}");
        }

        // The planner reports no delay and no platform on Italian stops, so the
        // first Italian train is looked up in the live sources. One extra call
        // buys the single fact a traveller about to leave actually needs.
        var firstLeg = journeys[0].Sections.FirstOrDefault(s => !s.IsWalk);
        var number = firstLeg?.Journey?.Number;

        if (!string.IsNullOrWhiteSpace(number) && when.Date == DateTimeOffset.UtcNow.Date)
        {
            var live = await trenord.GetTrainAsync(number, ct);
            var train = live.FirstOrDefault()?.Journeys.FirstOrDefault()?.Train;

            if (train?.Delay is { } delay)
                sb.AppendLine($"\n  Live: {firstLeg!.Journey!.Label} is running {FormatDelay(delay)} " +
                              $"({train.StatusText}).");
        }

        sb.AppendLine("\n  Times come from the Swiss open timetable, which carries no delays or " +
                      "platforms for Italian stops. Use get_train or get_departures for those.");

        // The planner knows its own network plus the cross-border lines, and
        // little else of Italy. Asked for two Italian places it will happily
        // route them through Switzerland — Como via Mendrisio in two hours,
        // when a domestic train does it in forty minutes. Say so rather than
        // letting a confident-looking itinerary stand.
        if (RoutedThroughSwitzerland(journeys[0]))
            sb.AppendLine("  This itinerary crosses into Switzerland. If both places are in Italy, " +
                          "a domestic route is almost certainly faster — check get_departures or " +
                          "find_connection, which read the Italian network directly.");

        return sb.ToString();
    }

    /// <summary>
    /// Plans from the regional timetable, or returns null to say it cannot: the
    /// caller then falls back to a planner with wider reach.
    ///
    /// Null means "ask someone else". A message means the question itself needs
    /// answering first — an ambiguous name, or two names for one station — and
    /// those are reported rather than guessed at.
    /// </summary>
    private async Task<string?> PlanFromTimetableAsync(
        string from, string to, DateTimeOffset when, int limit, CancellationToken ct)
    {
        var origin = await gtfs.FindStopsAsync(from, ct);
        if (origin.Count == 0) return null;

        var destination = await gtfs.FindStopsAsync(to, ct);
        if (destination.Count == 0) return null;

        if (origin.Count > 1) return Ambiguous(from, origin);
        if (destination.Count > 1) return Ambiguous(to, destination);

        if (string.Equals(origin[0].Id, destination[0].Id, StringComparison.OrdinalIgnoreCase))
            return $"'{from}' and '{to}' are the same station ({origin[0].Name}). " +
                   "Name two different places to plan a journey between them.";

        var journeys = await planner.PlanAsync(
            origin[0].Id, destination[0].Id, DateOnly.FromDateTime(when.DateTime),
            when.TimeOfDay, Math.Clamp(limit, 1, 6), ct);

        // Both places are in the regional timetable, so this is the source that
        // should answer and there is nothing better to defer to. Falling back
        // from here sends two Lombardy stations to a planner built for
        // Switzerland, which answers rather than declining: Chiavenna to Como
        // came back as seven hours through St. Moritz and Bellinzona.
        //
        // The honest answer is which limit was hit. "Not covered" would be
        // false — both are covered — and the caller would stop asking instead
        // of splitting the journey at an intermediate station.
        if (journeys.Count == 0)
            return $"No journey from {origin[0].Name} to {destination[0].Name} on " +
                   $"{Stamp(when)} with at most one change.\n" +
                   "Both stations are in the regional timetable, so this is not a gap in " +
                   "coverage: journeys needing two or more changes are not searched for. " +
                   "Asking again in two halves, through a station on the way, will find one " +
                   "if it exists.";

        // The timetable is an export and disagrees with the operator by a few
        // minutes on some trains. The feed now carries the train number, so each
        // leg can be asked about directly — but only for a day the live sources
        // still hold, and only for the journeys about to be shown.
        var rome = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ViaggiaTrenoClient.RomeTz);
        var checkable = (when.Date - rome.Date).TotalDays is >= 0 and <= 1;

        var status = new Dictionary<PlannedLeg, LegStatus>();
        if (checkable)
            foreach (var leg in journeys.SelectMany(j => j.Legs).Where(l => !l.IsBus).Distinct())
                if (await live.CheckAsync(leg, ct) is { } found)
                    status[leg] = found;

        var sb = new StringBuilder(
            $"{origin[0].Name} -> {destination[0].Name}, from {Stamp(when)}\n");

        foreach (var journey in journeys)
        {
            var changes = journey.Changes == 0 ? "direct" :
                journey.Changes == 1 ? "1 change" : $"{journey.Changes} changes";

            // The heading has to agree with the legs under it. Left as planned
            // while the legs show what the operator says, a journey announces
            // 08:53 and then lists a train arriving at 08:57.
            var starts = Shown(journey.Legs[0], status).Departure;
            var ends = Shown(journey.Legs[^1], status).Arrival;
            var d = (int)(ends - starts).TotalMinutes;
            var length = d < 60 ? $"{d}min" : $"{d / 60}h{d % 60:00}";

            sb.AppendLine(
                $"\n  {starts:hh\\:mm} -> {ends:hh\\:mm}   {length}, {changes}");

            for (var i = 0; i < journey.Legs.Count; i++)
            {
                var leg = journey.Legs[i];
                // A replacement coach is marked. It leaves from the forecourt,
                // not a platform, and a caller told only "TN Bus" reads it as
                // the name of a line.
                var mode = leg.IsBus ? "  [BUS]" : "";

                // Where the operator has been asked, its answer is the one shown
                // and the timetable's is kept alongside it: a leg the two
                // disagree about is exactly the leg not to build a tight change
                // on.
                var (departure, arrival) = Shown(leg, status);
                var note = "";

                if (status.TryGetValue(leg, out var real))
                {
                    if (departure != leg.Departure || arrival != leg.Arrival)
                        note += $"  (timetable: {leg.Departure:hh\\:mm}-{leg.Arrival:hh\\:mm})";
                    if (real.Platform is not null) note += $"  platform {real.Platform}";
                    if (real.Delay is { } late and not 0) note += $"  {FormatDelay(late)}";
                    if (real.Cancelled) note += "  ** CANCELLED **";
                }

                sb.AppendLine(
                    $"      {leg.Route,-6} {leg.Train,-6} {Trim(leg.FromStop, 22),-22} {departure:hh\\:mm}" +
                    $" -> {Trim(leg.ToStop, 22),-22} {arrival:hh\\:mm}{mode}{note}");

                if (i + 1 < journey.Legs.Count)
                {
                    var next = journey.Legs[i + 1];
                    var wait = (int)(Shown(next, status).Departure - arrival).TotalMinutes;
                    var warning = wait < MinimumChangeMinutes
                        ? "  ** too tight: the operator's times do not leave enough to change **"
                        : "";

                    sb.AppendLine($"      {"",-13} change at {leg.ToStop}, {wait} min{warning}");
                }
            }
        }

        sb.AppendLine(status.Count > 0
            ? "\n  Times shown are the operator's own, read live per train; where they differ " +
              "from the timetable this planned from, the timetable's are given in brackets. " +
              "A leg with no live answer is timetabled only."
            : "\n  These are timetabled times from the regional feed: no delays, no platforms, " +
              "and no account of a train cancelled today. The live sources are only asked for " +
              "today and tomorrow; for any other day, check with get_train nearer the time.");

        if (journeys.Any(j => j.HasBus))
            sb.AppendLine(
                "  A leg marked [BUS] is a replacement coach, not a train. It leaves from " +
                "outside the station rather than a platform, is not in the live train data, " +
                "and takes longer than the timetable suggests when the road is busy.");

        return sb.ToString();
    }

    /// <summary>
    /// Whether a name the planner resolved to is recognisably the one asked for.
    ///
    /// One substantial word in common is enough — "Zurich" against "Zürich HB",
    /// "Lugano" against "Lugano, Piazza Stazione" — while an answer about a
    /// different place shares nothing. Accents are folded because the request
    /// and the index rarely spell them the same way.
    /// </summary>
    private static bool Resembles(string requested, string? resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved)) return false;

        var wanted = Fold(requested).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                    .Where(w => w.Length >= 4).ToList();
        if (wanted.Count == 0) return true;   // nothing substantial to check against

        var got = Fold(resolved);
        return wanted.Any(w => got.Contains(w, StringComparison.Ordinal));
    }

    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var kept = decomposed.Where(c =>
            CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);

        return new string(kept.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ')
                              .ToArray());
    }

    /// <summary>
    /// The times to show for a leg: the operator's where it answered, the
    /// timetable's where it did not.
    /// </summary>
    private static (TimeSpan Departure, TimeSpan Arrival) Shown(
        PlannedLeg leg, IReadOnlyDictionary<PlannedLeg, LegStatus> status) =>
        status.TryGetValue(leg, out var real)
            ? (real.Departure ?? leg.Departure, real.Arrival ?? leg.Arrival)
            : (leg.Departure, leg.Arrival);

    private static string Ambiguous(string input, IReadOnlyList<GtfsStopMatch> matches)
    {
        var sb = new StringBuilder(
            $"'{input}' matches {matches.Count} stations in the regional timetable, which are " +
            "different places with different trains. Ask which one is meant:\n");
        foreach (var m in matches.Take(15))
            sb.AppendLine($"  {m.Id,-8} {m.Name}");
        if (matches.Count > 15) sb.AppendLine($"  ... and {matches.Count - 15} more");
        return sb.ToString();
    }

    /// <summary>
    /// True when any leg calls at a Swiss stop. Judged by the stop names the
    /// planner returns, which is crude but enough to flag the case that
    /// matters: an Italian journey needlessly routed abroad.
    /// </summary>
    private static bool RoutedThroughSwitzerland(SwissConnection journey)
    {
        string[] swiss = ["MENDRISIO", "CHIASSO", "LUGANO", "BELLINZONA", "STABIO", "BALERNA", "CAPOLAGO"];
        return journey.Sections.Any(s =>
            swiss.Any(name =>
                (s.Departure?.Name ?? "").ToUpperInvariant().Contains(name) ||
                (s.Arrival?.Name ?? "").ToUpperInvariant().Contains(name)));
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
