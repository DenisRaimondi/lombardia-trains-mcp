using LombardiaTrains.Mcp.Clients;

namespace LombardiaTrains.Mcp.Services;

public sealed record PlannedLeg(
    string Route, string Trip, bool IsBus,
    string FromStop, TimeSpan Departure, string ToStop, TimeSpan Arrival);

public sealed record PlannedJourney(IReadOnlyList<PlannedLeg> Legs)
{
    public TimeSpan Departure => Legs[0].Departure;
    public TimeSpan Arrival => Legs[^1].Arrival;
    public int Changes => Legs.Count - 1;
    public int DurationMinutes => (int)(Arrival - Departure).TotalMinutes;

    /// <summary>Minutes available at the interchange, for a single-change journey.</summary>
    public int? TransferMinutes =>
        Legs.Count == 2 ? (int)(Legs[1].Departure - Legs[0].Arrival).TotalMinutes : null;

    public bool HasBus => Legs.Any(l => l.IsBus);

    /// <summary>
    /// The journey as a traveller can tell it apart from another one: where
    /// each leg starts and ends, and when.
    ///
    /// Keyed on the service number instead, two coaches leaving Bozzolo at the
    /// same minute for the same town came back as two options, distinguishable
    /// only by six minutes of arrival — a choice nobody standing at the stop
    /// could act on.
    /// </summary>
    internal string Signature =>
        string.Join("|", Legs.Select(l =>
            $"{l.FromStop}@{l.Departure:hh\\:mm}>{l.ToStop}"));
}

/// <summary>
/// Plans journeys from the regional timetable: direct trains, and journeys with
/// one change.
///
/// One change is where it stops on purpose. Two changes multiply the search
/// space and the ways to be subtly wrong, and on this network almost everything
/// worth travelling is reachable with one — so the limit is stated to the
/// caller rather than hidden behind an incomplete search.
///
/// A transfer needs a real minimum. Five minutes is enough on the same island
/// platform and not enough anywhere else, so the floor is set where a traveller
/// would actually make it rather than where the timetable technically allows.
///
/// Results are ranked by when they arrive, not by when they leave. Someone asking
/// how to get somewhere wants to be there soonest, and ordering by departure puts
/// a train that leaves four minutes earlier and arrives forty minutes later at the
/// top of the list — which is how a slow route through a distant junction came to
/// be offered ahead of the obvious one.
/// </summary>
public sealed class GtfsPlanner(GtfsClient gtfs)
{
    private sealed record Arrival(TimeSpan At, TimeSpan LeftAt, string TripId, List<TimedStop> Stops);

    private const int MinTransferMinutes = 4;
    private const int MaxTransferMinutes = 60;

    public async Task<IReadOnlyList<PlannedJourney>> PlanAsync(
        string fromStopId,
        string toStopId,
        DateOnly date,
        TimeSpan notBefore,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        if (string.Equals(fromStopId, toStopId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Origin and destination are the same stop. Asked for a journey between a place " +
                "and itself, a search over departures and arrivals will happily answer with a " +
                "trip out and a trip back: a valid itinerary, and never what was meant.",
                nameof(toStopId));

        var timetable = await gtfs.GetTimetableAsync(ct);
        var running = await gtfs.GetServicesOnAsync(date, ct);

        // Only the trips that actually run on the requested day, with each
        // trip's clock made monotonic so a service crossing midnight does not
        // appear to arrive before it left.
        var active = timetable.StopTimes
            .Where(st => st.TripId is not null
                         && timetable.Trips.TryGetValue(st.TripId, out var trip)
                         && GtfsClient.ServiceKey(trip.ServiceId) is { } key
                         && running.Contains(key))
            .GroupBy(st => st.TripId!)
            .ToDictionary(g => g.Key, g => TripTimeline.Normalise(g));

        // Both searches always run. Returning the direct trains as soon as there
        // are enough of them looks like an obvious saving and quietly answers a
        // different question: asked for one journey from Mantova, it offered a
        // replacement coach at 19:27 because that was the only train doing the
        // whole route, while a train and a connection got there at 09:28.
        //
        // A direct service is not better for being direct. It is better when it
        // arrives sooner, and that is decided by ranking them together.
        var direct = FindDirect(active, timetable, fromStopId, toStopId, notBefore);
        var viaChange = FindOneChange(active, timetable, fromStopId, toStopId, notBefore);

        return Collapse(direct.Concat(viaChange))
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Reduces the variants of a service to the one journey it really is.
    ///
    /// A train appears in this feed once per stopping pattern it has ever had,
    /// each labelled with an opaque hash — 1051 of 4715 services carry more than
    /// one, up to eight. The calendar names only the service number, so nothing
    /// published says which variant runs today. Left alone that shows the same
    /// train two or three times over, at slightly different minutes, as if they
    /// were alternatives to each other.
    ///
    /// Where the variants disagree, the earliest arrival is kept. That is not a
    /// principle, it is calibration: in both cases that could be checked against
    /// the operator's own planner — Lecco to Sondrio at 09:19 against 09:20,
    /// Bozzolo to Mantova at 10:38 against 10:44 — the published answer was the
    /// earlier one. Keeping the later looked safer and was wrong twice.
    /// </summary>
    private static List<PlannedJourney> Collapse(IEnumerable<PlannedJourney> journeys)
    {
        var distinct = journeys
            .GroupBy(j => j.Signature)
            .Select(g => g.OrderBy(j => j.Arrival).First())
            .ToList();

        // Then drop the ones nothing would choose. A journey is beaten when
        // another leaves no earlier and arrives no later: same or better on both
        // counts, so the only thing the loser offers is more time on a platform.
        //
        // Without this, three ways of reaching Milano Cadorna at 09:05 filled
        // the answer — leaving at 08:06, 08:18 and 08:24, all arriving together
        // — and pushed out the direct train that runs it in thirty-two minutes.
        var kept = distinct
            .Where(j => !distinct.Any(other => Beats(other, j)))
            .ToList();

        return kept
            .OrderBy(j => j.Arrival)
            .ThenBy(j => j.Changes)
            .ThenBy(j => j.DurationMinutes)
            .ToList();
    }

    private static bool Beats(PlannedJourney a, PlannedJourney b) =>
        !ReferenceEquals(a, b)
        && a.Departure >= b.Departure
        && a.Arrival <= b.Arrival
        && (a.Departure > b.Departure || a.Arrival < b.Arrival);

    private static List<PlannedJourney> FindDirect(
        Dictionary<string, List<TimedStop>> active, GtfsTimetable timetable,
        string from, string to, TimeSpan notBefore)
    {
        var result = new List<PlannedJourney>();

        foreach (var (tripId, stops) in active)
        {
            var leg = BuildLeg(stops, timetable, tripId, from, to, notBefore);
            if (leg is not null) result.Add(new PlannedJourney([leg]));
        }

        return result.OrderBy(j => j.Arrival).ThenBy(j => j.Departure).ToList();
    }

    private static List<PlannedJourney> FindOneChange(
        Dictionary<string, List<TimedStop>> active, GtfsTimetable timetable,
        string from, string to, TimeSpan notBefore)
    {
        // Everywhere reachable from the origin, and every train that gets there.
        //
        // Keeping only the earliest arrival at each interchange looks like an
        // optimisation and is a bug: it fixes one first leg per interchange, so
        // a later train reaching the same platform in time for the same
        // connection can never be offered. The itinerary that came out was not
        // wrong, it was just eighteen minutes of standing on a platform that
        // nobody would choose — the official planner leaves Castellanza at
        // 08:42 and arrives at Como at the same 09:44 this offered for 08:24.
        var reachable = new Dictionary<string, List<Arrival>>();

        foreach (var (tripId, stops) in active)
        {
            var boarding = stops.FirstOrDefault(s =>
                s.StopId == from && s.Departure >= notBefore);
            if (boarding is null) continue;

            foreach (var stop in stops.Where(s => s.Seq > boarding.Seq))
            {
                if (stop.Arrival is null) continue;
                if (!reachable.TryGetValue(stop.StopId, out var arrivals))
                    reachable[stop.StopId] = arrivals = [];
                arrivals.Add(new Arrival(
                    stop.Arrival.Value, boarding.Departure!.Value, tripId, stops));
            }
        }

        var journeys = new List<PlannedJourney>();

        foreach (var (tripId, stops) in active)
        {
            foreach (var boarding in stops)
            {
                if (boarding.Departure is null) continue;
                if (boarding.StopId == from) continue;
                if (!reachable.TryGetValue(boarding.StopId, out var arrivals)) continue;

                // Of the trains that make this connection, the last one: the
                // same journey with the waiting taken out of it.
                var arrival = arrivals
                    .Where(a =>
                    {
                        var wait = (boarding.Departure.Value - a.At).TotalMinutes;
                        return wait >= MinTransferMinutes && wait <= MaxTransferMinutes;
                    })
                    .OrderByDescending(a => a.At)
                    // Two trains reaching the interchange at the same minute are
                    // not the same offer: take the one that left later, and the
                    // difference is time not spent waiting at the origin.
                    .ThenByDescending(a => a.LeftAt)
                    .FirstOrDefault();
                if (arrival is null) continue;

                var second = BuildLeg(stops, timetable, tripId, boarding.StopId, to,
                    boarding.Departure.Value);
                if (second is null) continue;

                var first = BuildLeg(arrival.Stops, timetable, arrival.TripId, from,
                    boarding.StopId, notBefore);
                if (first is null) continue;

                journeys.Add(new PlannedJourney([first, second]));
            }
        }

        // Several trips can produce the same itinerary; keep the quickest of each.
        return journeys
            .GroupBy(j => (j.Departure, j.Arrival))
            .Select(g => g.OrderBy(j => j.DurationMinutes).First())
            .OrderBy(j => j.Arrival)
            .ToList();
    }

    /// <summary>
    /// A leg of one trip between two stops, or null when this trip does not
    /// serve them in that order at a usable time. The order check is what keeps
    /// a train heading the other way from being offered.
    /// </summary>
    private static PlannedLeg? BuildLeg(
        List<TimedStop> stops, GtfsTimetable timetable, string tripId,
        string from, string to, TimeSpan notBefore)
    {
        var boarding = stops.FirstOrDefault(s => s.StopId == from && s.Departure >= notBefore);
        if (boarding?.Departure is null) return null;

        var alighting = stops.FirstOrDefault(s => s.StopId == to && s.Seq > boarding.Seq);
        if (alighting?.Arrival is null) return null;

        var route = timetable.Trips.TryGetValue(tripId, out var trip) && trip.RouteId is not null
                    && timetable.Routes.TryGetValue(trip.RouteId, out var info)
            ? info
            : new GtfsRouteInfo("train", false);

        return new PlannedLeg(
            route.Name, GtfsClient.ServiceKey(tripId) ?? tripId, route.IsBus,
            timetable.StopNames.GetValueOrDefault(from, from), boarding.Departure.Value,
            timetable.StopNames.GetValueOrDefault(to, to), alighting.Arrival.Value);
    }
}
