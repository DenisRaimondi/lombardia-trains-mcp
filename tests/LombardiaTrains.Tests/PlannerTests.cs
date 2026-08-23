using LombardiaTrains.Mcp.Clients;
using LombardiaTrains.Mcp.Services;
using Xunit.Abstractions;

namespace LombardiaTrains.Tests;

public class GtfsPlannerTests(ITestOutputHelper output)
{
    private static GtfsPlanner NewPlanner() => new(new GtfsClient(new HttpClient()));

    [Fact]
    public async Task Plans_a_journey_that_needs_a_change()
    {
        // Castellanza to Como Lago has no direct service: it requires changing.
        var journeys = await NewPlanner().PlanAsync(
            "S01136", "S01765", new DateOnly(2026, 8, 29), new TimeSpan(9, 0, 0));

        foreach (var j in journeys)
        {
            output.WriteLine($"{j.Departure:hh\\:mm} -> {j.Arrival:hh\\:mm}  " +
                             $"{j.DurationMinutes}min, {j.Changes} change(s), " +
                             $"transfer {j.TransferMinutes}min");
            foreach (var leg in j.Legs)
                output.WriteLine($"    {leg.Route,-8} {leg.FromStop} {leg.Departure:hh\\:mm}" +
                                 $" -> {leg.ToStop} {leg.Arrival:hh\\:mm}");
        }

        Assert.NotEmpty(journeys);
    }

    [Fact]
    public async Task Plans_across_the_Swiss_border()
    {
        // Busto Arsizio to Lugano: the regional feed reaches into Ticino.
        var journeys = await NewPlanner().PlanAsync(
            "S01031", "S10000", new DateOnly(2026, 8, 29), new TimeSpan(9, 0, 0), maxResults: 3);

        foreach (var j in journeys)
        {
            output.WriteLine($"{j.Departure:hh\\:mm} -> {j.Arrival:hh\\:mm}  {j.Changes} change(s)");
            foreach (var leg in j.Legs)
                output.WriteLine($"    {leg.Route,-8} {leg.FromStop} {leg.Departure:hh\\:mm}" +
                                 $" -> {leg.ToStop} {leg.Arrival:hh\\:mm}");
        }
    }
}
