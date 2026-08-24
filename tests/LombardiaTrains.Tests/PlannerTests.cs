using LombardiaTrains.Mcp.Clients;
using LombardiaTrains.Mcp.Services;
using Xunit.Abstractions;

namespace LombardiaTrains.Tests;

/// <summary>
/// Checks the planner against the live timetable.
///
/// Nothing here asserts a departure time. The feed is republished daily and a
/// test written around 08:24 fails the week the timetable changes, which teaches
/// the reader to ignore it. What is asserted instead are the properties that
/// must hold whatever the timetable says: time moves forward, a change is long
/// enough to make and short enough to accept, the journey starts and ends where
/// it was asked to, and the list is ordered the way it claims to be.
///
/// Those are also exactly the properties that were broken. A midnight wrap made
/// arrival precede departure; ordering by departure put a slower journey first.
/// Both would fail here.
/// </summary>
public class GtfsPlannerTests(ITestOutputHelper output)
{
    private static readonly GtfsClient Shared = new(new HttpClient());
    private static GtfsPlanner NewPlanner() => new(Shared);

    // Codes shared by this feed and ViaggiaTreno.
    private const string Castellanza = "S01136";
    private const string Saronno = "S01933";
    private const string ComoLago = "S01765";
    private const string BustoArsizio = "S01031";
    private const string Lugano = "S05300";
    private const string MilanoCadorna = "S01066";
    private const string MalpensaT2 = "S01146";
    private const string VareseNord = "S01738";

    private static readonly DateOnly Weekday = NextWeekday();
    private static readonly TimeSpan Morning = new(8, 0, 0);

    /// <summary>
    /// A fixed date would expire. A Sunday would exercise a thinner timetable
    /// than the one most of these assertions describe, so the next weekday is
    /// used and the test keeps meaning the same thing next month.
    /// </summary>
    private static DateOnly NextWeekday()
    {
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            day = day.AddDays(1);
        return day;
    }

    private void Dump(string label, IReadOnlyList<PlannedJourney> journeys)
    {
        output.WriteLine($"\n### {label}  [{Weekday:yyyy-MM-dd}]  {journeys.Count} found");
        foreach (var j in journeys)
        {
            output.WriteLine($"    {j.Departure:hh\\:mm} -> {j.Arrival:hh\\:mm}  " +
                             $"{j.DurationMinutes,3}min, {j.Changes} change");
            foreach (var leg in j.Legs)
                output.WriteLine($"        {leg.Route,-8} {leg.FromStop,-24} {leg.Departure:hh\\:mm}" +
                                 $" -> {leg.ToStop,-24} {leg.Arrival:hh\\:mm}");
        }
    }

    // ------------------------------------------------------------ invariants

    [Fact]
    public async Task Time_only_ever_moves_forward()
    {
        var journeys = await NewPlanner().PlanAsync(Castellanza, ComoLago, Weekday, Morning, 6);
        Dump("Castellanza -> Como Lago", journeys);
        Assert.NotEmpty(journeys);

        foreach (var j in journeys)
        {
            Assert.True(j.Arrival > j.Departure,
                $"journey arrives {j.Arrival} having left {j.Departure}");

            foreach (var leg in j.Legs)
                Assert.True(leg.Arrival > leg.Departure,
                    $"leg {leg.Route} arrives {leg.Arrival} having left {leg.Departure}");

            for (var i = 1; i < j.Legs.Count; i++)
                Assert.True(j.Legs[i].Departure >= j.Legs[i - 1].Arrival,
                    $"leg {i} leaves {j.Legs[i].Departure} before leg {i - 1} arrives {j.Legs[i - 1].Arrival}");
        }
    }

    [Fact]
    public async Task Every_change_is_long_enough_to_make_and_short_enough_to_accept()
    {
        var journeys = await NewPlanner().PlanAsync(Castellanza, ComoLago, Weekday, Morning, 6);

        foreach (var j in journeys.Where(j => j.Changes == 1))
        {
            var wait = j.TransferMinutes!.Value;
            Assert.InRange(wait, 4, 60);
        }
    }

    [Fact]
    public async Task Results_are_ordered_by_arrival()
    {
        var journeys = await NewPlanner().PlanAsync(Castellanza, ComoLago, Weekday, Morning, 6);

        var arrivals = journeys.Select(j => j.Arrival).ToList();
        Assert.Equal(arrivals.OrderBy(a => a).ToList(), arrivals);
    }

    [Fact]
    public async Task A_journey_starts_and_ends_where_it_was_asked_to()
    {
        var planner = NewPlanner();
        var timetable = await new GtfsClient(new HttpClient()).GetTimetableAsync();
        var journeys = await planner.PlanAsync(Castellanza, ComoLago, Weekday, Morning, 6);

        foreach (var j in journeys)
        {
            Assert.Equal(timetable.StopNames[Castellanza], j.Legs[0].FromStop);
            Assert.Equal(timetable.StopNames[ComoLago], j.Legs[^1].ToStop);
        }
    }

    [Fact]
    public async Task Nothing_leaves_before_the_time_asked_for()
    {
        var after = new TimeSpan(14, 0, 0);
        var journeys = await NewPlanner().PlanAsync(Castellanza, ComoLago, Weekday, after, 6);

        Assert.All(journeys, j => Assert.True(j.Departure >= after,
            $"asked for departures after {after}, got {j.Departure}"));
    }

    [Fact]
    public async Task No_more_than_one_change_is_ever_offered()
    {
        var journeys = await NewPlanner().PlanAsync(Castellanza, Lugano, Weekday, Morning, 6);
        Assert.All(journeys, j => Assert.InRange(j.Changes, 0, 1));
    }

    [Fact]
    public async Task The_same_service_is_never_offered_twice()
    {
        // A train appears in the feed once per stopping pattern it has ever had,
        // and nothing published says which one runs today. Uncollapsed, the same
        // departure comes back two or three times a minute apart, presented as a
        // choice between alternatives that do not exist.
        var journeys = await NewPlanner().PlanAsync(MilanoCadorna, VareseNord, Weekday, Morning, 6);
        Dump("Milano Cadorna -> Varese Nord", journeys);

        var signatures = journeys.Select(j => j.Signature).ToList();
        Assert.Equal(signatures.Distinct().Count(), signatures.Count);

        var departures = journeys.Select(j => (j.Departure, j.Legs[0].Route)).ToList();
        Assert.Equal(departures.Distinct().Count(), departures.Count);
    }

    [Fact]
    public async Task A_replacement_coach_is_not_passed_off_as_a_train()
    {
        // Nearly a quarter of this feed is TN_Bus. Whether one turns up on a
        // given route on a given day is not for a test to depend on, so this
        // asserts the weaker thing that must always hold: whatever is returned
        // knows which of the two it is, and a bus is never labelled rail.
        var timetable = await Shared.GetTimetableAsync();
        var buses = timetable.Routes.Values.Where(r => r.IsBus).ToList();

        Assert.NotEmpty(buses);
        Assert.All(buses, b => Assert.Contains("Bus", b.Name, StringComparison.OrdinalIgnoreCase));

        var rail = timetable.Routes.Values.Where(r => !r.IsBus);
        Assert.All(rail, r => Assert.DoesNotContain("Bus", r.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Nothing_is_offered_that_another_journey_beats_outright()
    {
        // Leaving no earlier and arriving no later means the other journey is
        // better on both counts, and this one only offers more waiting. Three
        // ways of reaching Milano Cadorna at 09:05 once filled the answer
        // between them and pushed out the direct train.
        foreach (var (from, to) in new[] { (Castellanza, MilanoCadorna), (Castellanza, ComoLago) })
        {
            var journeys = await NewPlanner().PlanAsync(from, to, Weekday, Morning, 6);

            foreach (var j in journeys)
                Assert.DoesNotContain(journeys, other =>
                    !ReferenceEquals(other, j)
                    && other.Departure >= j.Departure && other.Arrival <= j.Arrival
                    && (other.Departure > j.Departure || other.Arrival < j.Arrival));
        }
    }

    [Fact]
    public async Task A_direct_train_is_not_crowded_out_by_journeys_with_a_change()
    {
        // Castellanza to Milano Cadorna runs direct in thirty-two minutes. A
        // connection arriving seven minutes earlier is a fair answer; three of
        // them, leaving at different times to arrive together, are not.
        var journeys = await NewPlanner().PlanAsync(Castellanza, MilanoCadorna, Weekday, Morning, 3);
        Dump("Castellanza -> Milano Cadorna", journeys);

        Assert.Contains(journeys, j => j.Changes == 0);
    }

    // ------------------------------------------------- routes that must work

    [Theory]
    [InlineData(Castellanza, Saronno, "Castellanza -> Saronno")]
    [InlineData(Saronno, ComoLago, "Saronno -> Como Lago")]
    [InlineData(Castellanza, MilanoCadorna, "Castellanza -> Milano Cadorna")]
    [InlineData(MilanoCadorna, MalpensaT2, "Milano Cadorna -> Malpensa T2")]
    public async Task A_route_with_a_direct_train_is_answered_directly(
        string from, string to, string label)
    {
        var journeys = await NewPlanner().PlanAsync(from, to, Weekday, Morning, 4);
        Dump(label, journeys);

        Assert.NotEmpty(journeys);
        Assert.Contains(journeys, j => j.Changes == 0);
    }

    [Fact]
    public async Task The_cross_border_line_is_planned_as_one_train()
    {
        var saturday = NextDayOfWeek(DayOfWeek.Saturday);
        var journeys = await NewPlanner().PlanAsync(
            BustoArsizio, Lugano, saturday, new TimeSpan(9, 0, 0), 4);
        Dump("Busto Arsizio -> Lugano, Saturday", journeys);

        Assert.NotEmpty(journeys);
        Assert.Contains(journeys, j => j.Changes == 0);
    }

    [Fact]
    public async Task A_change_is_found_where_no_direct_train_exists()
    {
        var journeys = await NewPlanner().PlanAsync(Castellanza, ComoLago, Weekday, Morning, 6);

        Assert.NotEmpty(journeys);
        Assert.All(journeys, j => Assert.Equal(1, j.Changes));
    }

    [Fact]
    public async Task Waiting_is_not_added_where_a_later_train_makes_the_same_connection()
    {
        // Comparing against the published planner turned this up: both found the
        // same connection out of Saronno, but this one boarded a train from
        // Castellanza eighteen minutes earlier to reach it. The itinerary was
        // valid and nobody would choose it.
        //
        // So for every journey with a change, no train may leave the origin
        // later and still make the same second leg.
        var journeys = await NewPlanner().PlanAsync(Castellanza, ComoLago, Weekday, Morning, 6);
        var timetable = await Shared.GetTimetableAsync();
        var running = await Shared.GetServicesOnAsync(Weekday);

        foreach (var j in journeys.Where(j => j.Changes == 1))
        {
            var interchange = timetable.StopNames.First(kv => kv.Value == j.Legs[0].ToStop).Key;
            var origin = timetable.StopNames[Castellanza];

            var later = timetable.StopTimes
                .Where(st => st.TripId is not null
                             && timetable.Trips.TryGetValue(st.TripId, out var t)
                             && GtfsClient.ServiceKey(t.ServiceId) is { } k && running.Contains(k))
                .GroupBy(st => st.TripId!)
                .Select(g => TripTimeline.Normalise(g))
                .Select(stops =>
                {
                    var board = stops.FirstOrDefault(s => s.StopId == Castellanza
                                                          && s.Departure > j.Departure);
                    if (board?.Departure is null) return ((TimeSpan?)null, (TimeSpan?)null);
                    var off = stops.FirstOrDefault(s => s.StopId == interchange && s.Seq > board.Seq);
                    return (board.Departure, off?.Arrival);
                })
                .Where(x => x.Item1 is not null && x.Item2 is not null)
                .Where(x => (j.Legs[1].Departure - x.Item2!.Value).TotalMinutes >= 4
                            && (j.Legs[1].Departure - x.Item2!.Value).TotalMinutes <= 60)
                .ToList();

            Assert.True(later.Count == 0,
                $"leaving {origin} at {j.Departure} for the {j.Legs[1].Departure} out of " +
                $"{j.Legs[0].ToStop}, when a train at {later.FirstOrDefault().Item1} makes it too");
        }
    }

    // ------------------------------------------------------------ the edges

    [Fact]
    public async Task A_journey_to_the_same_place_is_refused()
    {
        // Left to the search this comes back as a train out and a train back:
        // a real itinerary, and never the question that was asked.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewPlanner().PlanAsync(Castellanza, Castellanza, Weekday, Morning));
    }

    [Fact]
    public async Task An_unknown_stop_finds_nothing_rather_than_throwing()
    {
        var journeys = await NewPlanner().PlanAsync("S99999", ComoLago, Weekday, Morning);
        Assert.Empty(journeys);
    }

    [Fact]
    public async Task A_question_asked_before_dawn_is_answered_with_the_first_service()
    {
        var journeys = await NewPlanner().PlanAsync(
            Castellanza, ComoLago, Weekday, new TimeSpan(3, 0, 0), 3);

        Assert.NotEmpty(journeys);
        Assert.All(journeys, j => Assert.True(j.Departure > new TimeSpan(4, 0, 0),
            $"a train at {j.Departure} is a midnight wrap read as an early morning"));
    }

    // ------------------------------------------------------- name resolution

    [Fact]
    public async Task An_exact_name_settles_a_search_that_would_otherwise_be_ambiguous()
    {
        // "Como Lago" is contained in nothing else, but "Saronno" is a prefix of
        // "Saronno Sud": an exact match has to win outright.
        var matches = await Shared.FindStopsAsync("Saronno");
        Assert.Single(matches);
        Assert.Equal(Saronno, matches[0].Id);
    }

    [Fact]
    public async Task A_partial_name_returns_the_stations_it_could_mean()
    {
        var matches = await Shared.FindStopsAsync("Como");

        Assert.Contains(matches, m => m.Id == ComoLago);
        Assert.True(matches.Count > 1, "Como has several stations and should not resolve silently");
    }

    [Fact]
    public async Task A_search_does_not_reach_inside_words()
    {
        // "Como" appears in "S.Giacomo Di Teglio". Offered as an alternative to
        // the city, it is not a near miss, it is a different province.
        var matches = await Shared.FindStopsAsync("Como");

        Assert.DoesNotContain(matches, m => m.Name.Contains("Giacomo", StringComparison.OrdinalIgnoreCase));
    }

    private static DateOnly NextDayOfWeek(DayOfWeek target)
    {
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        while (day.DayOfWeek != target) day = day.AddDays(1);
        return day;
    }
}
