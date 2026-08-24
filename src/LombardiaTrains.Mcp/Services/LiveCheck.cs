using System.Globalization;
using LombardiaTrains.Mcp.Clients;
using LombardiaTrains.Mcp.Models;

namespace LombardiaTrains.Mcp.Services;

/// <summary>
/// What the operator says about one planned leg today.
/// </summary>
public sealed record LegStatus(
    TimeSpan? Departure, TimeSpan? Arrival, int? Delay, bool Cancelled, string? Platform);

/// <summary>
/// Checks planned legs against the operator's live data.
///
/// The timetable this plans from is an export, and an export is not the thing it
/// was exported from. Measured against the operator's own planner on the same
/// day: train 2217 is timed at 48 minutes into Bergamo where the operator says
/// 52; train 10668 arrives at Mortara five minutes later in the feed than in the
/// operator's answer; train 10719 reaches Ponte S.Pietro at 08:52 in the feed
/// and its connecting coach leaves at 08:51, a change the feed itself makes
/// impossible and the operator has working with five minutes to spare.
///
/// None of that is recoverable from open timetable data — but the live sources
/// are the operator's own, and the feed now carries the train number, so each
/// leg can simply be looked up and asked.
///
/// This corrects what is shown and catches what has changed today: a cancelled
/// train, a platform, a delay, a connection that no longer works. It does not
/// recover journeys the feed's errors excluded before the search ever saw them,
/// because the correction happens after the planning and not before it.
/// </summary>
public sealed class LiveCheck(TrenordClient trenord)
{
    public async Task<LegStatus?> CheckAsync(PlannedLeg leg, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leg.Train)) return null;

        IReadOnlyList<TrenordRun> runs;
        try
        {
            runs = await trenord.GetTrainAsync(leg.Train, ct);
        }
        catch (HttpRequestException)
        {
            // A live source that is down must not take the plan down with it.
            return null;
        }

        foreach (var stops in runs.SelectMany(r => r.Journeys).Select(j => j.Stops))
        {
            var from = stops.FirstOrDefault(s => Same(s.Station?.Name, leg.FromStop));
            var to = stops.FirstOrDefault(s => Same(s.Station?.Name, leg.ToStop));
            if (from is null || to is null) continue;

            var delay = from.ActualData?.DepDelay ?? to.ActualData?.ArrDelay;

            return new LegStatus(
                Clock(from.DepTime), Clock(to.ArrTime), delay,
                from.Cancelled == true || to.Cancelled == true,
                string.IsNullOrWhiteSpace(from.Platform) ? null : from.Platform);
        }

        return null;
    }

    /// <summary>
    /// The two sources spell a station differently — "Ponte S.Pietro" against
    /// "PONTE S.PIETRO" — so names are compared with case and punctuation set
    /// aside rather than matched literally.
    /// </summary>
    private static bool Same(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return Fold(a) == Fold(b);
    }

    private static string Fold(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static TimeSpan? Clock(string? raw) =>
        TimeSpan.TryParseExact(raw, [@"hh\:mm\:ss", @"hh\:mm"],
            CultureInfo.InvariantCulture, out var value) ? value : null;
}
