using LombardiaTrains.Mcp.Clients;

namespace LombardiaTrains.Mcp.Services;

public sealed record PlannedLeg(
    string Route, string FromStop, TimeSpan Departure, string ToStop, TimeSpan Arrival);

public sealed record PlannedJourney(IReadOnlyList<PlannedLeg> Legs)
{
    public TimeSpan Departure => Legs[0].Departure;
    public TimeSpan Arrival => Legs[^1].Arrival;
    public int Changes => Legs.Count - 1;
    public int DurationMinutes => (int)(Arrival - Departure).TotalMinutes;

    /// <summary>Minutes available at the interchange, for a single-change journey.</summary>
    public int? TransferMinutes =>
        Legs.Count == 2 ? (int)(Legs[1].Departure - Legs[0].Arrival).TotalMinutes : null;
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

        var direct = FindDirect(active, timetable, fromStopId, toStopId, notBefore);
        if (direct.Count >= maxResults)
            return direct.Take(maxResults).ToList();

        var viaChange = FindOneChange(active, timetable, fromStopId, toStopId, notBefore);

        return direct.Concat(viaChange)
            .OrderBy(j => j.Arrival)
            .ThenBy(j => j.Changes)
            .ThenBy(j => j.DurationMinutes)
            .Take(maxResults)
            .ToList();
    }

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
        // Everywhere reachable from the origin, with the earliest arrival at each.
        var reachable = new Dictionary<string, (TimeSpan Arrival, string TripId, List<TimedStop> Stops)>();

        foreach (var (tripId, stops) in active)
        {
            var boarding = stops.FirstOrDefault(s =>
                s.StopId == from && s.Departure >= notBefore);
            if (boarding is null) continue;

            foreach (var stop in stops.Where(s => s.Seq > boarding.Seq))
            {
                if (stop.Arrival is null) continue;
                if (!reachable.TryGetValue(stop.StopId, out var best) || stop.Arrival < best.Arrival)
                    reachable[stop.StopId] = (stop.Arrival.Value, tripId, stops);
            }
        }

        var journeys = new List<PlannedJourney>();

        foreach (var (tripId, stops) in active)
        {
            foreach (var boarding in stops)
            {
                if (boarding.Departure is null) continue;
                if (boarding.StopId == from) continue;
                if (!reachable.TryGetValue(boarding.StopId, out var arrival)) continue;

                var wait = (boarding.Departure.Value - arrival.Arrival).TotalMinutes;
                if (wait < MinTransferMinutes || wait > MaxTransferMinutes) continue;

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
                    && timetable.RouteNames.TryGetValue(trip.RouteId, out var name)
            ? name
            : "train";

        return new PlannedLeg(
            route,
            timetable.StopNames.GetValueOrDefault(from, from), boarding.Departure.Value,
            timetable.StopNames.GetValueOrDefault(to, to), alighting.Arrival.Value);
    }
}
