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
