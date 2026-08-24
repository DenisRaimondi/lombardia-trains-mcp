using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;

namespace LombardiaTrains.Mcp.Clients;

/// <summary>
/// Reads the regional rail timetable Trenord publishes as GTFS, under CC0, on
/// dati.lombardia.it.
///
/// It is read as the zip the operator generates, not through the portal's
/// table-per-file API. The tables look easier — paged JSON, filterable — and are
/// a lossy import of this same file:
///
///   * a quarter of the timetable is missing: 68,952 stop times against 90,553
///     here, 6,265 trips against 8,470. Trips arrive truncated, so a train that
///     runs Varese to Milano and beyond appeared to terminate halfway, and
///     everything reachable by staying on it was invisible;
///   * times land inside a placeholder date, which cannot express the 24:05 that
///     GTFS uses for a service past midnight, so it comes back as 00:05 and the
///     train arrives before it left;
///   * service ids are rewritten with an opaque hash, and no longer match the
///     ones in calendar_dates, so the two files cannot be joined on the key they
///     are supposed to share;
///   * the decimal point is dropped from coordinates;
///   * trip_short_name — the train number, the one thing that connects a planned
///     journey to the live data — is not carried over at all.
///
/// None of that is in the file itself. The stop ids are the codes ViaggiaTreno
/// uses, so a planned journey joins to live delays and platforms with no
/// translation table.
/// </summary>
public sealed class GtfsClient
{
    private const string FeedUrl = "https://www.dati.lombardia.it/download/3z4k-mxz9/application%2Fzip";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private IReadOnlyList<GtfsStopTime>? _stopTimes;
    private IReadOnlyDictionary<string, GtfsTrip>? _trips;
    private IReadOnlyDictionary<string, GtfsRouteInfo>? _routes;
    private IReadOnlyDictionary<string, string>? _stopNames;
    private IReadOnlyDictionary<string, GtfsStop>? _stops;
    private IReadOnlyDictionary<string, HashSet<string>>? _servicesByDate;

    public GtfsClient(HttpClient http)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("lombardia-trains-mcp/1.0");
        _http.Timeout = TimeSpan.FromMinutes(3);
    }

    /// <summary>
    /// Loads the feed once and keeps it. Around 90k stop times and 8k trips:
    /// small enough to hold, and holding it turns every later question into a
    /// local lookup rather than a request.
    /// </summary>
    public async Task<GtfsTimetable> GetTimetableAsync(CancellationToken ct = default)
    {
        if (_stopTimes is null) await LoadAsync(ct);
        return new GtfsTimetable(_stopTimes!, _trips!, _routes!, _stopNames!);
    }

    public async Task<IReadOnlyDictionary<string, GtfsStop>> GetStopsAsync(CancellationToken ct = default)
    {
        if (_stops is null) await LoadAsync(ct);
        return _stops!;
    }

    /// <summary>
    /// Services running on a date.
    ///
    /// The feed carries no calendar.txt: every service-date pair is listed
    /// explicitly in calendar_dates instead of as exceptions to a weekly
    /// pattern. That is a legitimate way to write GTFS, and it means this is the
    /// whole answer rather than half of one.
    /// </summary>
    public async Task<HashSet<string>> GetServicesOnAsync(DateOnly date, CancellationToken ct = default)
    {
        if (_servicesByDate is null) await LoadAsync(ct);

        var key = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return _servicesByDate!.TryGetValue(key, out var services) ? services : [];
    }

    /// <summary>
    /// Stops whose name matches, exact matches first.
    ///
    /// Containment alone picks the wrong station. A name that is a prefix of
    /// another one — and on this network several are — matches both, and the
    /// longer one can come first, so a request for one town returns the times of
    /// a different station in it. An exact match therefore settles the question
    /// before containment is considered at all.
    /// </summary>
    public async Task<IReadOnlyList<GtfsStopMatch>> FindStopsAsync(
        string query, CancellationToken ct = default)
    {
        var timetable = await GetTimetableAsync(ct);
        var needle = query.Trim();

        var exact = timetable.StopNames
            .Where(kv => string.Equals(kv.Value.Trim(), needle, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new GtfsStopMatch(kv.Key, kv.Value))
            .ToList();

        if (exact.Count > 0) return exact;

        return timetable.StopNames
            .Where(kv => StartsAWord(kv.Value, needle))
            .Select(kv => new GtfsStopMatch(kv.Key, kv.Value))
            .OrderBy(m => m.Name.Length)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when the name contains the query starting at a word boundary.
    ///
    /// Plain containment reaches inside words and returns places that merely
    /// share a run of letters: searching for one town brings back two villages
    /// whose names happen to end in the same syllable.
    /// </summary>
    private static bool StartsAWord(string name, string needle)
    {
        var at = 0;
        while ((at = name.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (at == 0 || !char.IsLetter(name[at - 1])) return true;
            at++;
        }
        return false;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            if (_stopTimes is not null) return;

            await using var stream = await _http.GetStreamAsync(FeedUrl, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

            var stops = Read(archive, "stops.txt", r => new GtfsStop(
                r["stop_id"], r["stop_name"], Coordinate(r, "stop_lat"), Coordinate(r, "stop_lon")))
                .Where(s => s.Id.Length > 0)
                .ToDictionary(s => s.Id, s => s);

            var routes = Read(archive, "routes.txt", r => (
                Id: r["route_id"],
                Info: new GtfsRouteInfo(
                    Blank(r["route_short_name"]) ?? Blank(r["route_long_name"]) ?? r["route_id"],
                    r["route_type"] == "3")))
                .Where(x => x.Id.Length > 0)
                .ToDictionary(x => x.Id, x => x.Info);

            var trips = Read(archive, "trips.txt", r => new GtfsTrip(
                r["trip_id"], r["route_id"], r["service_id"], Blank(r["trip_short_name"])))
                .Where(t => t.TripId.Length > 0)
                .ToDictionary(t => t.TripId, t => t);

            var stopTimes = Read(archive, "stop_times.txt", r => new GtfsStopTime(
                r["trip_id"], r["stop_id"],
                int.TryParse(r["stop_sequence"], out var seq) ? seq : 0,
                Clock(r["arrival_time"]), Clock(r["departure_time"])))
                .Where(st => st.TripId.Length > 0)
                .ToList();

            var byDate = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var row in Read(archive, "calendar_dates.txt",
                         r => (Service: r["service_id"], Date: r["date"], Type: r["exception_type"])))
            {
                if (row.Type != "1") continue;
                if (!byDate.TryGetValue(row.Date, out var set))
                    byDate[row.Date] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(row.Service);
            }

            _stops = stops;
            _stopNames = stops.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
            _routes = routes;
            _trips = trips;
            _stopTimes = stopTimes;
            _servicesByDate = byDate;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? Coordinate(IReadOnlyDictionary<string, string> row, string field) =>
        double.TryParse(row.GetValueOrDefault(field), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// Reads a GTFS time. Hours run past 24 for a service that crosses midnight
    /// — 24:05 is five past midnight on the day the trip started — which is the
    /// whole point of the format and is why TimeSpan.Parse cannot be used: it
    /// treats three parts as h:m:s and overflows on anything past 23.
    /// </summary>
    private static TimeSpan? Clock(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split(':');
        if (parts.Length < 2) return null;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            return null;

        var s = parts.Length > 2 &&
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec)
            ? sec : 0;

        return new TimeSpan(h, m, s);
    }

    private static IEnumerable<T> Read<T>(
        ZipArchive archive, string name, Func<IReadOnlyDictionary<string, string>, T> map)
    {
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"the feed does not contain {name}");

        using var reader = new StreamReader(entry.Open());

        var header = ReadRow(reader);
        if (header is null) yield break;

        // The first field can carry a byte-order mark, which would otherwise
        // make the column called "trip_id" unfindable by that name.
        if (header.Count > 0) header[0] = header[0].TrimStart('﻿');

        var results = new List<T>();
        while (ReadRow(reader) is { } row)
        {
            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++)
                record[header[i]] = i < row.Count ? row[i] : "";
            results.Add(map(record));
        }

        foreach (var r in results) yield return r;
    }

    /// <summary>
    /// One CSV record. Quoted fields may contain commas and doubled quotes, and
    /// station names in this feed do contain commas.
    /// </summary>
    private static List<string>? ReadRow(StreamReader reader)
    {
        var line = reader.ReadLine();
        if (line is null) return null;

        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c != '"') { field.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; continue; }
                quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }

        fields.Add(field.ToString());
        return fields;
    }
}

public sealed record GtfsStopMatch(string Id, string Name);

public sealed record GtfsStop(string Id, string Name, double? Lat, double? Lon);

public sealed record GtfsTimetable(
    IReadOnlyList<GtfsStopTime> StopTimes,
    IReadOnlyDictionary<string, GtfsTrip> Trips,
    IReadOnlyDictionary<string, GtfsRouteInfo> Routes,
    IReadOnlyDictionary<string, string> StopNames);

/// <summary>
/// A line, and whether it is actually a train.
///
/// Nearly a quarter of the trips in this feed are on route TN_Bus, "TN Bus
/// sostitutivi": coaches replacing trains where a line is closed. They are the
/// real service on the day they run, so leaving them out would be worse than
/// including them — but they are not trains, they do not leave from a platform,
/// and a nine-minute connection onto one is a different proposition. GTFS says
/// which is which in route_type: 2 is rail, 3 is bus.
/// </summary>
public sealed record GtfsRouteInfo(string Name, bool IsBus);

/// <summary>
/// A trip. <paramref name="ShortName"/> carries the number the train is known
/// by — "RE_2 - 2217" — which is what lets a planned leg be handed to get_train.
/// </summary>
public sealed record GtfsTrip(string TripId, string RouteId, string ServiceId, string? ShortName);

public sealed record GtfsStopTime(
    string TripId, string StopId, int Seq, TimeSpan? Arrival, TimeSpan? Departure);
