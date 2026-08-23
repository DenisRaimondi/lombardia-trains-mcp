using LombardiaTrains.Mcp.Clients;
using LombardiaTrains.Mcp.Services;
using Xunit.Abstractions;

namespace LombardiaTrains.Tests;

public class GtfsPlannerTests(ITestOutputHelper output)
{
    private static readonly GtfsClient Shared = new(new HttpClient());
    private static GtfsPlanner NewPlanner() => new(Shared);

    private async Task Show(string label, string from, string to,
        DateOnly date, TimeSpan after, int limit = 3)
    {
        var journeys = await NewPlanner().PlanAsync(from, to, date, after, limit);
        output.WriteLine($"\n### {label}  [{date:yyyy-MM-dd} after {after:hh\\:mm}]");

        if (journeys.Count == 0) { output.WriteLine("    nothing found"); return; }

        foreach (var j in journeys)
        {
            var t = j.TransferMinutes is { } m ? $", transfer {m}min" : "";
            output.WriteLine($"    {j.Departure:hh\\:mm} -> {j.Arrival:hh\\:mm}  " +
                             $"{j.DurationMinutes,3}min, {j.Changes} change{t}");
            foreach (var leg in j.Legs)
                output.WriteLine($"        {leg.Route,-8} {leg.FromStop,-22} {leg.Departure:hh\\:mm}" +
                                 $" -> {leg.ToStop,-22} {leg.Arrival:hh\\:mm}");
        }
    }

    [Fact]
    public async Task Battery()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var sat = new DateOnly(2026, 8, 29);
        var now = DateTime.Now.TimeOfDay;

        // --- the case that started this ---
        await Show("Castellanza -> Como Lago, NOW", "S01136", "S01765", today, now);
        await Show("Castellanza -> Como Lago, tomorrow 08:00", "S01136", "S01765",
            today.AddDays(1), new TimeSpan(8, 0, 0));

        // --- known-direct routes, to catch false negatives ---
        await Show("Castellanza -> Milano Cadorna", "S01136", "S01066", today.AddDays(1), new TimeSpan(8, 0, 0));
        await Show("Castellanza -> Saronno", "S01136", "S01933", today.AddDays(1), new TimeSpan(8, 0, 0));
        await Show("Saronno -> Como Lago", "S01933", "S01765", today.AddDays(1), new TimeSpan(8, 0, 0));

        // --- cross-border ---
        await Show("Busto Arsizio -> Lugano, Saturday", "S01031", "S05300", sat, new TimeSpan(9, 0, 0));
        await Show("Milano Cadorna -> Malpensa T2", "S01066", "S01146", today.AddDays(1), new TimeSpan(7, 0, 0));

        // --- edges: nothing should blow up ---
        // A journey from a place to itself is refused rather than answered. Left to
        // the search, it comes back with a train out and a train back.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewPlanner().PlanAsync("S01136", "S01136", today.AddDays(1), new TimeSpan(8, 0, 0)));
        await Show("Castellanza -> Como at 03:00 (no service)", "S01136", "S01765", today.AddDays(1), new TimeSpan(3, 0, 0));
        await Show("unknown stop id", "S99999", "S01765", today.AddDays(1), new TimeSpan(8, 0, 0));
    }
}
