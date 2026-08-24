using LombardiaTrains.Mcp.Clients;
using LombardiaTrains.Mcp.Services;
using LombardiaTrains.Mcp.Tools;
using Xunit.Abstractions;

namespace LombardiaTrains.Tests;

/// <summary>
/// Exercises the tools as a model would call them, against the live services.
///
/// What a tool returns is prose, and prose read by a model is an interface: a
/// sentence that quietly stops saying "no direct train" is a change in
/// behaviour even though every type still compiles. So these assert on the
/// claims the text makes, not on its wording, and never on a time that the
/// timetable is free to change tomorrow.
///
/// The failure modes matter more than the successes here. A tool that invents
/// a station, resolves an ambiguous name by picking the first, or answers a
/// question about a place it does not cover is worse than one that returns
/// nothing, because nothing downstream can tell that it was wrong.
/// </summary>
public class ToolTests(ITestOutputHelper output)
{
    private static TrainTools NewTools()
    {
        var viaggiaTreno = new ViaggiaTrenoClient(new HttpClient());
        var trenord = new TrenordClient(new HttpClient());
        var swiss = new SwissTransportClient(new HttpClient());
        var gtfs = new GtfsClient(new HttpClient());
        return new TrainTools(
            viaggiaTreno, trenord, swiss,
            new ConnectionFinder(viaggiaTreno, trenord),
            gtfs, new GtfsPlanner(gtfs));
    }

    private static string Tomorrow(string time) =>
        $"{DateOnly.FromDateTime(DateTime.Now).AddDays(1):yyyy-MM-dd}T{time}";

    private void Show(string label, string text)
    {
        output.WriteLine($"\n===== {label} =====\n{text}");
    }

    // ----------------------------------------------------------------- time

    [Fact]
    public void Now_states_the_zone_it_is_talking_about()
    {
        var text = NewTools().Now();
        Show("now", text);

        // A bare time is worse than useless to a caller in another zone: it
        // looks usable and is wrong by an hour or two.
        Assert.Contains("Europe/Rome", text);
        Assert.Contains("ISO:", text);
        Assert.Contains(DateTime.Now.Year.ToString(), text);
    }

    // -------------------------------------------------------------- lookups

    [Fact]
    public async Task A_station_is_found_by_name()
    {
        var text = await NewTools().SearchStationAsync("Milano Centrale");
        Show("search_station", text);
        Assert.Contains("S01700", text);
    }

    [Fact]
    public async Task A_name_that_matches_nothing_says_where_coverage_ends()
    {
        var text = await NewTools().SearchStationAsync("Barcelona Sants");
        Show("search_station, off the network", text);

        // The point is not the phrasing, it is that the answer contains no
        // station at all and explains itself.
        Assert.DoesNotContain("S0", text);
        Assert.Contains("network", text, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------- boards

    [Fact]
    public async Task A_departure_board_names_the_station_it_is_for()
    {
        var text = await NewTools().GetDeparturesAsync("S01700", null, 5);
        Show("get_departures", text);

        Assert.Contains("S01700", text);
        Assert.DoesNotContain("Exception", text);
    }

    [Fact]
    public async Task An_arrival_board_names_the_station_it_is_for()
    {
        var text = await NewTools().GetArrivalsAsync("S01700", null, 5);
        Show("get_arrivals", text);
        Assert.Contains("S01700", text);
    }

    [Fact]
    public async Task An_ambiguous_station_name_is_returned_as_a_question()
    {
        var text = await NewTools().GetDeparturesAsync("Milano", null, 5);
        Show("get_departures, ambiguous", text);

        // Several stations in one city, served by different trains: picking the
        // first would produce a board that is entirely plausible and not the
        // one asked for.
        Assert.Contains("matches", text);
        Assert.Contains("S0", text);
    }

    // ------------------------------------------------------------- journeys

    [Fact]
    public async Task A_journey_needing_a_change_is_composed()
    {
        var text = await NewTools().FindJourneyAsync(
            "Castellanza", "Como Lago", Tomorrow("08:00"));
        Show("find_journey, one change", text);

        Assert.Contains("Castellanza", text);
        Assert.Contains("Como Lago", text);
        Assert.Contains("change", text);

        // Scheduled times carry no delays, and a caller that does not know
        // that will trust a four-minute transfer it should not.
        Assert.Contains("get_departures", text);
    }

    [Fact]
    public async Task A_cross_border_journey_is_planned_without_leaving_the_regional_timetable()
    {
        var saturday = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        while (saturday.DayOfWeek != DayOfWeek.Saturday) saturday = saturday.AddDays(1);

        var text = await NewTools().FindJourneyAsync(
            "Busto Arsizio", "Lugano", $"{saturday:yyyy-MM-dd}T09:00");
        Show("find_journey, cross-border", text);

        Assert.Contains("Lugano", text);
        Assert.Contains("direct", text);
    }

    [Fact]
    public async Task A_destination_off_the_regional_network_falls_back_rather_than_failing()
    {
        var text = await NewTools().FindJourneyAsync(
            "Milano Centrale", "Zurich", Tomorrow("08:00"));
        Show("find_journey, fallback", text);

        Assert.Contains("Z", text);      // Zürich HB, however the source spells it
        Assert.DoesNotContain("No journey found", text);
    }

    [Fact]
    public async Task An_ambiguous_place_is_not_planned_for_silently()
    {
        var text = await NewTools().FindJourneyAsync("Como", "Milano Cadorna", Tomorrow("08:00"));
        Show("find_journey, ambiguous origin", text);

        Assert.Contains("matches", text);
        Assert.Contains("S01765", text);           // Como Lago is one of the options
        Assert.DoesNotContain("->", text);         // and no itinerary was invented
    }

    [Fact]
    public async Task A_journey_from_a_place_to_itself_is_refused()
    {
        var text = await NewTools().FindJourneyAsync("Castellanza", "Castellanza", Tomorrow("08:00"));
        Show("find_journey, same place", text);

        Assert.Contains("same station", text);
    }

    [Theory]
    [InlineData("Milano Centrale", "Roma Termini")]
    [InlineData("Roma Termini", "Napoli Centrale")]
    [InlineData("Torino Porta Nuova", "Genova Piazza Principe")]
    public async Task A_journey_across_Italy_is_declined_rather_than_answered_about_Switzerland(
        string from, string to)
    {
        // The planner behind the fallback covers Switzerland and reaches into
        // Italy near the border. Asked about anywhere else it does not decline:
        // it resolves the name to the nearest thing in its own index and answers
        // about that. "Roma Termini" became a beauty clinic in Locarno, with a
        // two-hour itinerary to reach it.
        var text = await NewTools().FindJourneyAsync(from, to, Tomorrow("08:00"));
        Show($"find_journey, {from} -> {to}", text);

        // The refusal names what it discarded, on purpose, so the message may
        // well mention Locarno. What must not be there is an itinerary: no
        // legs, no times, nothing that could be read as an answer.
        // Echoing back the time that was asked for is fine. An itinerary is
        // not: every leg carries a departure and an arrival, so no line may
        // hold two clock times, and none may hold an arrow.
        Assert.DoesNotContain("->", text);
        Assert.All(text.Split('\n'), line =>
            Assert.True(System.Text.RegularExpressions.Regex.Matches(line, @"\d\d:\d\d").Count < 2,
                $"this reads as a leg of a journey: {line}"));

        // And declining is not enough on its own: for these two stations the
        // live tools work perfectly well, and saying only "not covered" reads
        // as though nothing here can help.
        Assert.Contains("get_departures", text);
        Assert.Contains("get_train", text);
    }

    [Fact]
    public async Task Guarding_the_fallback_does_not_close_the_border()
    {
        // The guard cannot be "is it Italian": the Swiss index holds Zurich and
        // ViaggiaTreno holds Zurich Altstetten. What it checks is whether the
        // answer is about the places that were asked for.
        var text = await NewTools().FindJourneyAsync("Milano Centrale", "Zurich", Tomorrow("08:00"));
        Show("find_journey, Milano -> Zurich", text);

        Assert.DoesNotContain("Cannot plan", text);
        Assert.Contains("rich", text);          // Zurich, Zürich, however spelled
    }

    // ------------------------------------------------------------ direct-only

    [Fact]
    public async Task Find_connection_says_it_cannot_see_a_change()
    {
        // Castellanza to Como Lago has no direct train. The honest answer is
        // that this tool cannot find one, not that none exists.
        var text = await NewTools().FindConnectionAsync(
            "Castellanza", "Como Lago", Tomorrow("08:00"));
        Show("find_connection, no direct", text);

        Assert.Contains("change", text, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------- trains

    [Fact]
    public async Task An_unknown_train_number_is_reported_as_unknown()
    {
        var text = await NewTools().GetTrainAsync("999999");
        Show("get_train, unknown", text);

        Assert.DoesNotContain("Exception", text);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    // ------------------------------------------------------- argument parsing

    [Theory]
    [InlineData("08:00")]
    [InlineData("8:00")]
    [InlineData("2026-08-29T09:00")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("some time next week")]
    public void Every_shape_of_time_argument_produces_a_usable_instant(string? at)
    {
        // A model will pass all of these. None may throw: an unparseable time
        // falls back to now, which is wrong by hours at worst and never fatal.
        var when = TrainTools.ParseWhen(at);
        Assert.True(when.Year >= DateTime.Now.Year);
    }

    [Fact]
    public void An_hour_and_minute_is_read_as_today_in_Rome()
    {
        var when = TrainTools.ParseWhen("08:00");
        var rome = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ViaggiaTrenoClient.RomeTz);

        Assert.Equal(8, when.Hour);
        Assert.Equal(rome.Date, when.Date);
        Assert.Equal(rome.Offset, when.Offset);
    }
}
