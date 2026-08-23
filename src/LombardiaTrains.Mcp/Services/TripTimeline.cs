using LombardiaTrains.Mcp.Clients;

namespace LombardiaTrains.Mcp.Services;

/// <summary>One stop of a trip, with its times made monotonic.</summary>
public sealed record TimedStop(string StopId, int Seq, TimeSpan? Arrival, TimeSpan? Departure);

public static class TripTimeline
{
    /// <summary>
    /// Puts a trip's stops in order and unwraps times that crossed midnight.
    ///
    /// GTFS expresses a service running past midnight as 24:05 or 25:30. This
    /// feed cannot: it stores times inside a placeholder date, so 24:05 comes
    /// back as 00:05 and a train that left at 23:50 appears to arrive fifteen
    /// hours earlier than it departed.
    ///
    /// Read literally, that poisons any search built on "earliest arrival". The
    /// soonest a planner could reach Saronno from Castellanza looked like 00:05,
    /// because one late-night service wrapped — and every real morning
    /// connection then fell outside the transfer window, so a journey with an
    /// obvious change simply came back empty.
    ///
    /// Times are therefore made monotonic along the trip: whenever the clock
    /// goes backwards between consecutive stops, a day is added from there on.
    /// A caller comparing against a wall clock should take the result modulo 24
    /// hours.
    /// </summary>
    public static List<TimedStop> Normalise(IEnumerable<GtfsStopTime> stops)
    {
        var ordered = stops.OrderBy(s => s.Seq).ToList();
        var result = new List<TimedStop>(ordered.Count);

        var dayOffset = TimeSpan.Zero;
        TimeSpan? previous = null;

        foreach (var stop in ordered)
        {
            if (stop.StopId is null) continue;

            var arrival = Shift(stop.Arrival, ref previous, ref dayOffset);
            var departure = Shift(stop.Departure, ref previous, ref dayOffset);

            result.Add(new TimedStop(stop.StopId, stop.Seq, arrival, departure));
        }

        return result;
    }

    private static TimeSpan? Shift(TimeSpan? raw, ref TimeSpan? previous, ref TimeSpan dayOffset)
    {
        if (raw is null) return null;

        // A step backwards of more than a few minutes means the clock wrapped.
        // Small backward steps do happen in published data and are noise, not
        // a new day.
        if (previous is { } last && raw.Value + dayOffset < last - TimeSpan.FromMinutes(5))
            dayOffset += TimeSpan.FromDays(1);

        var shifted = raw.Value + dayOffset;
        previous = shifted;
        return shifted;
    }
}
