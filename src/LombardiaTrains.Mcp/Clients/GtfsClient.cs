using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LombardiaTrains.Mcp.Clients;

/// <summary>
/// Reads Regione Lombardia's regional rail timetable, published as GTFS under
/// CC0 on dati.lombardia.it.
///
/// This is the only source here that holds a complete timetable: every trip,
/// every stop, every time. The live APIs answer "what is happening at this
/// station right now"; this answers "what is scheduled, anywhere, on any day",
/// which is what planning a journey with a change requires.
///
/// Its stop_ids are the same codes ViaggiaTreno uses — S01136 is Castellanza in
/// both — so a planned journey joins directly to live delays and platforms with
/// no translation table.
///
/// Three things about the published data are worth knowing:
///
///  * Times carry a placeholder date: "1899-12-31T06:05:00.000" means 06:05.
///  * calendar is unusable — the weekday and validity columns were lost in
///    publication, leaving only service_id. It does not matter, because the
///    feed lists every service-date pair in calendar_dates instead.
///  * Dates in calendar_dates are strings shaped "20260829", not ISO. Querying
///    for "2026-08-29" returns zero rows rather than an error.
/// </summary>
public sealed class GtfsClient
{
    private const string BaseUrl = "https://www.dati.lombardia.it/resource";
    private const string StopTimes = "4z9q-hrcb";
    private const string Trips = "asyc-aywm";
    private const string Routes = "yqye-t4rp";
    private const string Stops = "j5jz-kvqn";
    private const string CalendarDates = "ucn3-apr6";

    /// <summary>Socrata caps a page at 50k rows, and stop_times is larger.</summary>
    private const int PageSize = 50_000;

    private readonly HttpClient _http;

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private IReadOnlyList<GtfsStopTime>? _stopTimes;
    private IReadOnlyDictionary<string, GtfsTrip>? _trips;
    private IReadOnlyDictionary<string, GtfsRouteInfo>? _routes;
    private IReadOnlyDictionary<string, string>? _stopNames;
    private readonly ConcurrentDictionary<string, HashSet<string>> _servicesByDate = new();

    public GtfsClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(BaseUrl + "/");
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("lombardia-trains-mcp/1.0");
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    /// <summary>
    /// Loads the timetable once and keeps it. Around 69k stop times and 6k
    /// trips: small enough to hold in memory, and holding it turns every
    /// subsequent question into a local lookup rather than a request.
    /// </summary>
    public async Task<GtfsTimetable> GetTimetableAsync(CancellationToken ct = default)
    {
        if (_stopTimes is not null)
            return new GtfsTimetable(_stopTimes, _trips!, _routes!, _stopNames!);

        await _loadGate.WaitAsync(ct);
        try
        {
            if (_stopTimes is null)
            {
                var stopTimes = await FetchAllAsync<GtfsStopTime>(StopTimes, ct);
                var trips = await FetchAllAsync<GtfsTrip>(Trips, ct);
                var routes = await FetchAllAsync<GtfsRoute>(Routes, ct);
                var stops = await FetchAllAsync<GtfsStop>(Stops, ct);

                _stopTimes = stopTimes;
                _trips = trips.Where(t => t.TripId is not null)
                              .ToDictionary(t => t.TripId!, t => t);
                _routes = routes.Where(r => r.RouteId is not null)
                                .ToDictionary(r => r.RouteId!, r => new GtfsRouteInfo(
                                    r.ShortName ?? r.LongName ?? r.RouteId!,
                                    r.RouteType == "3"));
                _stopNames = stops.Where(s => s.StopId is not null)
                                  .ToDictionary(s => s.StopId!, s => s.StopName ?? s.StopId!);
            }
        }
        finally
        {
            _loadGate.Release();
        }

        return new GtfsTimetable(_stopTimes!, _trips!, _routes!, _stopNames!);
    }

    /// <summary>
    /// Services running on a date, as join keys.
    ///
    /// The two files disagree about how a service is named. trips writes
    /// "124865-0b0cb949", calendar_dates writes "124865-2026-08-21-2026-08-30":
    /// the same service, suffixed with a hash in one export and with its
    /// validity period in the other. Joining on the full string matches nothing
    /// at all, so both sides are reduced to the part before the first hyphen,
    /// which is the service number they share.
    ///
    /// Past that, the feed lists every service-date pair explicitly, so this is
    /// the whole answer rather than exceptions layered over a weekly pattern.
    /// </summary>
    public async Task<HashSet<string>> GetServicesOnAsync(DateOnly date, CancellationToken ct = default)
    {
        var key = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_servicesByDate.TryGetValue(key, out var cached)) return cached;

        var rows = await FetchAllAsync<GtfsCalendarDate>(
            CalendarDates, ct, $"$where=date='{key}' AND exception_type='1'");

        var services = rows.Select(r => ServiceKey(r.ServiceId))
                           .Where(s => s is not null)
                           .Select(s => s!)
                           .ToHashSet(StringComparer.Ordinal);

        _servicesByDate[key] = services;
        return services;
    }

    /// <summary>
    /// Stops whose name matches, exact matches first.
    ///
    /// Containment alone picks the wrong station. A name that is a prefix of
    /// another one — and on this network several are — matches both, and the
    /// longer one can come first, so a request for one town returns the times
    /// of a different station in it. An exact match therefore settles the
    /// question before containment is considered at all.
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
    /// whose names happen to end in the same syllable. Requiring the match to
    /// begin a word keeps a partial name useful without that.
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

    /// <summary>The service number the two files agree on.</summary>
    public static string? ServiceKey(string? serviceId)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) return null;
        var cut = serviceId.IndexOf('-');
        return cut > 0 ? serviceId[..cut] : serviceId;
    }

    private async Task<List<T>> FetchAllAsync<T>(
        string dataset, CancellationToken ct, string? filter = null)
    {
        var all = new List<T>();
        var offset = 0;

        while (true)
        {
            var query = $"{dataset}.json?$limit={PageSize}&$offset={offset}" +
                        (filter is null ? "" : "&" + filter);

            var page = await _http.GetFromJsonAsync<List<T>>(query, ct) ?? [];
            all.AddRange(page);

            if (page.Count < PageSize) break;
            offset += PageSize;
        }

        return all;
    }
}

public sealed record GtfsStopMatch(string Id, string Name);

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

public sealed class GtfsStopTime
{
    [JsonPropertyName("trip_id")] public string? TripId { get; init; }
    [JsonPropertyName("stop_id")] public string? StopId { get; init; }
    [JsonPropertyName("stop_sequence")] public string? Sequence { get; init; }

    /// <summary>"1899-12-31T06:05:00.000" — only the time part means anything.</summary>
    [JsonPropertyName("arrival_time")] public string? ArrivalRaw { get; init; }
    [JsonPropertyName("departure_time")] public string? DepartureRaw { get; init; }

    [JsonIgnore] public int Seq => int.TryParse(Sequence, out var s) ? s : 0;
    [JsonIgnore] public TimeSpan? Arrival => ParseTime(ArrivalRaw);
    [JsonIgnore] public TimeSpan? Departure => ParseTime(DepartureRaw);

    /// <summary>
    /// Takes the time out of the placeholder date. A GTFS time may legitimately
    /// exceed 24 hours for a service running past midnight; the published form
    /// cannot express that, so anything before 03:00 is left as it is and the
    /// caller compares within a single day.
    /// </summary>
    private static TimeSpan? ParseTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Contains('T') ? raw[(raw.IndexOf('T') + 1)..] : raw;
        if (t.Length >= 8) t = t[..8];
        return TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts) ? ts : null;
    }
}

public sealed class GtfsTrip
{
    [JsonPropertyName("trip_id")] public string? TripId { get; init; }
    [JsonPropertyName("route_id")] public string? RouteId { get; init; }
    [JsonPropertyName("service_id")] public string? ServiceId { get; init; }
}

public sealed class GtfsRoute
{
    [JsonPropertyName("route_id")] public string? RouteId { get; init; }
    [JsonPropertyName("route_short_name")] public string? ShortName { get; init; }
    [JsonPropertyName("route_long_name")] public string? LongName { get; init; }
    [JsonPropertyName("route_type")] public string? RouteType { get; init; }
}

public sealed class GtfsStop
{
    [JsonPropertyName("stop_id")] public string? StopId { get; init; }
    [JsonPropertyName("stop_name")] public string? StopName { get; init; }
}

public sealed class GtfsCalendarDate
{
    [JsonPropertyName("service_id")] public string? ServiceId { get; init; }
    [JsonPropertyName("date")] public string? Date { get; init; }
    [JsonPropertyName("exception_type")] public string? ExceptionType { get; init; }
}
