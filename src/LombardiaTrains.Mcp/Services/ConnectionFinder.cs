using System.Globalization;
using System.Text;
using LombardiaTrains.Mcp.Clients;

namespace LombardiaTrains.Mcp.Services;

public sealed record Connection(
    string Train,
    string DepartureTime,
    string ArrivalTime,
    string? Platform,
    int? Delay,
    int? DurationMinutes,
    bool Cancelled,
    int Stops);

/// <summary>
/// Finds direct trains between two stations.
///
/// ViaggiaTreno used to expose a journey-planning endpoint (soluzioniViaggio)
/// and it now answers "Error" for every argument shape tried, so connections
/// are composed here instead: read the departure board at the origin, then ask
/// each candidate train for its own stop list and keep the ones that call at
/// the destination after the origin.
///
/// That costs one request per candidate, which is why the board is truncated
/// before the lookups start and the lookups run a few at a time rather than
/// all at once.
/// </summary>
public sealed class ConnectionFinder(ViaggiaTrenoClient viaggiaTreno, TrenordClient trenord)
{
    private const int MaxConcurrentLookups = 4;

    public async Task<IReadOnlyList<Connection>> FindAsync(
        string originCode,
        string originName,
        string destinationName,
        DateTimeOffset when,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        var board = await viaggiaTreno.GetDeparturesAsync(originCode, when, ct);
        if (board.Count == 0) return [];

        // Look at more departures than we intend to return: most of them will
        // not call at the destination.
        var candidates = board.Take(Math.Clamp(maxResults * 4, 8, 20)).ToList();

        var found = new List<Connection>();
        using var gate = new SemaphoreSlim(MaxConcurrentLookups);

        var lookups = candidates.Select(async row =>
        {
            if (row.TrainNumber is null) return null;

            await gate.WaitAsync(ct);
            try
            {
                return await BuildConnectionAsync(
                    row.TrainNumber.Value.ToString(CultureInfo.InvariantCulture),
                    row.Category, row.DepartureTime, row.Platform, row.Delay,
                    row.IsCancelled, originName, destinationName, ct);
            }
            catch (Exception) when (ct.IsCancellationRequested is false)
            {
                // One unreachable train must not sink the whole search.
                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var connection in await Task.WhenAll(lookups))
            if (connection is not null)
                found.Add(connection);

        return found
            .OrderBy(c => c.DepartureTime, StringComparer.Ordinal)
            .Take(maxResults)
            .ToList();
    }

    private async Task<Connection?> BuildConnectionAsync(
        string trainNumber, string? category, string? boardDeparture, string? platform,
        int? delay, bool cancelled, string originName, string destinationName,
        CancellationToken ct)
    {
        // Trenord first: it covers the regional fleet this is mostly used for,
        // and it returns the whole stop list in one call.
        var runs = await trenord.GetTrainAsync(trainNumber, ct);
        foreach (var run in runs)
        {
            var stops = run.Journeys.FirstOrDefault()?.Stops;
            if (stops is null || stops.Count == 0) continue;

            var names = stops.Select(s => s.Station?.Name).ToList();
            var times = stops.Select(s => Hhmm(s.DepTime ?? s.ArrTime)).ToList();
            var (from, to) = Locate(names, times, originName, destinationName, boardDeparture);
            if (to < 0) continue;

            var dep = (from > 0 ? Hhmm(stops[from].DepTime ?? stops[from].ArrTime) : null)
                      ?? boardDeparture ?? Hhmm(stops[from].DepTime ?? stops[from].ArrTime);
            var arr = Hhmm(stops[to].ArrTime ?? stops[to].DepTime);
            var line = run.Journeys.FirstOrDefault()?.Train?.Line;

            return new Connection(
                $"{line ?? category}{trainNumber}".Trim(),
                dep ?? "--:--", arr ?? "--:--",
                stops[from].Platform ?? platform,
                run.Journeys.FirstOrDefault()?.Train?.Delay ?? delay,
                Minutes(dep, arr),
                cancelled || stops[to].Cancelled == true,
                to - from);
        }

        // Fall back to ViaggiaTreno for anything Trenord does not know about.
        var progress = await viaggiaTreno.GetTrainProgressAsync(trainNumber, ct);
        if (progress is null || progress.Stops.Count == 0) return null;

        var vtNames = progress.Stops.Select(s => s.Station).ToList();
        var vtTimes = progress.Stops
            .Select(s => ViaggiaTrenoClient.ToRomeHhMm(s.ScheduledDeparture ?? s.Scheduled))
            .ToList();
        var (vtFrom, vtTo) = Locate(vtNames, vtTimes!, originName, destinationName, boardDeparture);
        if (vtTo < 0) return null;

        var vtDep = vtFrom > 0
            ? ViaggiaTrenoClient.ToRomeHhMm(
                progress.Stops[vtFrom].ScheduledDeparture ?? progress.Stops[vtFrom].Scheduled)
            : boardDeparture ?? ViaggiaTrenoClient.ToRomeHhMm(
                progress.Stops[vtFrom].ScheduledDeparture ?? progress.Stops[vtFrom].Scheduled);
        var vtArr = ViaggiaTrenoClient.ToRomeHhMm(
            progress.Stops[vtTo].ScheduledArrival ?? progress.Stops[vtTo].Scheduled);

        return new Connection(
            $"{progress.Category}{trainNumber}".Trim(),
            vtDep, vtArr,
            progress.Stops[vtFrom].Platform,
            progress.Delay ?? delay,
            Minutes(vtDep, vtArr),
            cancelled || progress.IsCancelled,
            vtTo - vtFrom);
    }

    /// <summary>
    /// Returns the index of origin and destination in a stop list.
    ///
    /// The origin may be unfindable by name — the caller can legitimately pass
    /// a station code, and codes never appear in a stop list. That is not a
    /// failure: the train was read off that station's own departure board, so
    /// it calls there by definition. In that case the search starts from the
    /// first stop and only the destination has to be found.
    ///
    /// Returns To = -1 when the destination is absent or comes before the
    /// origin, which is what makes this a direction-aware search rather than a
    /// "does this train touch both" one.
    /// </summary>
    private static (int From, int To) Locate(
        IReadOnlyList<string?> stopNames,
        IReadOnlyList<string?> stopTimes,
        string origin,
        string destination,
        string? boardDeparture)
    {
        var wanted = Normalise(destination);
        var start = Normalise(origin);

        var from = -1;
        for (var i = 0; i < stopNames.Count; i++)
        {
            if (!Matches(stopNames[i], start)) continue;
            from = i;
            break;
        }

        if (from >= 0)
        {
            for (var i = from + 1; i < stopNames.Count; i++)
                if (Matches(stopNames[i], wanted))
                    return (from, i);

            return (from, -1);
        }

        // The origin could not be matched by name, which happens whenever the
        // caller passed a station code. The train still calls there — it came
        // off that station's own board — so its position is fixed by time
        // instead: the destination has to be reached after the board time.
        //
        // Without this the search matches stops the train has already left. A
        // Malpensa-bound service that STARTS at Milano Cadorna would otherwise
        // be offered as a way of reaching Milano Cadorna, arriving before it
        // departed.
        for (var i = 0; i < stopNames.Count; i++)
        {
            if (!Matches(stopNames[i], wanted)) continue;
            if (IsAfter(stopTimes.ElementAtOrDefault(i), boardDeparture))
                return (0, i);
        }

        return (-1, -1);
    }

    /// <summary>True when <paramref name="candidate"/> is at or after
    /// <paramref name="reference"/>, both as "HH:mm". Unparseable values are
    /// treated as acceptable rather than silently dropping a real train.</summary>
    private static bool IsAfter(string? candidate, string? reference)
    {
        if (!TimeSpan.TryParse(candidate, CultureInfo.InvariantCulture, out var c)) return true;
        if (!TimeSpan.TryParse(reference, CultureInfo.InvariantCulture, out var r)) return true;
        return c >= r;
    }

    /// <summary>
    /// Station names are spelled differently by the two sources: "MILANO
    /// CADORNA" here, "Milano Cadorna FN" there, "MILANO NORD CADORNA"
    /// elsewhere. Comparison is done on normalised text and accepts either
    /// side containing the other, which is loose enough for real timetables
    /// without matching unrelated stations.
    /// </summary>
    private static bool Matches(string? stopName, string normalisedWanted)
    {
        if (string.IsNullOrWhiteSpace(stopName) || normalisedWanted.Length == 0) return false;
        var actual = Normalise(stopName);
        return actual.Contains(normalisedWanted, StringComparison.Ordinal)
            || normalisedWanted.Contains(actual, StringComparison.Ordinal);
    }

    internal static string Normalise(string value)
    {
        var decomposed = value.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }

        return sb.ToString().Trim();
    }

    private static string? Hhmm(string? time) =>
        string.IsNullOrWhiteSpace(time) ? null : time[..Math.Min(5, time.Length)];

    private static int? Minutes(string? from, string? to)
    {
        if (!TimeSpan.TryParse(from, CultureInfo.InvariantCulture, out var a)) return null;
        if (!TimeSpan.TryParse(to, CultureInfo.InvariantCulture, out var b)) return null;

        var delta = b - a;
        if (delta < TimeSpan.Zero) delta += TimeSpan.FromDays(1);   // journey crosses midnight
        return (int)delta.TotalMinutes;
    }
}
