using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LombardiaTrains.Mcp.Models;

namespace LombardiaTrains.Mcp.Clients;

/// <summary>
/// Client for ViaggiaTreno, the RFI/Trenitalia public endpoint. No auth,
/// plain HTTP. It covers the RFI network and, somewhat unpredictably, part of
/// the FNM lines: for a Trenord train start from <see cref="TrenordClient"/>,
/// and come here for station boards, which Trenord does not expose.
/// </summary>
public sealed class ViaggiaTrenoClient
{
    private const string BaseUrl = "http://www.viaggiatreno.it/infomobilita/resteasy/viaggiatreno";

    /// <summary>All times published by this API are Italian local times.</summary>
    public static readonly TimeZoneInfo RomeTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public ViaggiaTrenoClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(BaseUrl + "/");
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("lombardia-trains-mcp/1.0");
    }

    public async Task<IReadOnlyList<VtStation>> SearchStationAsync(
        string name, CancellationToken ct = default)
    {
        var url = $"cercaStazione/{Uri.EscapeDataString(name)}";
        var stations = await SafeGetAsync<List<VtStation>>(url, ct);
        return stations ?? [];
    }

    public Task<IReadOnlyList<VtBoardRow>> GetDeparturesAsync(
        string stationCode, DateTimeOffset when, CancellationToken ct = default) =>
        GetBoardAsync("partenze", stationCode, when, ct);

    public Task<IReadOnlyList<VtBoardRow>> GetArrivalsAsync(
        string stationCode, DateTimeOffset when, CancellationToken ct = default) =>
        GetBoardAsync("arrivi", stationCode, when, ct);

    private async Task<IReadOnlyList<VtBoardRow>> GetBoardAsync(
        string kind, string stationCode, DateTimeOffset when, CancellationToken ct)
    {
        var url = $"{kind}/{stationCode}/{Uri.EscapeDataString(FormatTimestamp(when))}";
        var rows = await SafeGetAsync<List<VtBoardRow>>(url, ct);
        return rows ?? [];
    }

    /// <summary>
    /// Live progress of a train. Needs the origin station code and the
    /// departure timestamp, which only the autocomplete endpoint knows, so
    /// this makes two calls.
    /// </summary>
    public async Task<VtTrainProgress?> GetTrainProgressAsync(
        string trainNumber, CancellationToken ct = default)
    {
        var runs = await LocateTrainAsync(trainNumber, ct);
        if (runs.Count == 0) return null;

        var run = runs[0];
        return await SafeGetAsync<VtTrainProgress>(
            $"andamentoTreno/{run.OriginCode}/{run.Number}/{run.DepartureMillis}", ct);
    }

    /// <summary>
    /// The autocomplete endpoint answers with plain text, not JSON. One line
    /// per run, in the shape:
    ///
    ///     11866 - PIOLTELLO LIMITO - 10/08/26|11866-S01703-1786312800000
    ///
    /// The half after the pipe carries the three values andamentoTreno needs.
    /// </summary>
    public async Task<IReadOnlyList<TrainRunRef>> LocateTrainAsync(
        string trainNumber, CancellationToken ct = default)
    {
        string body;
        try
        {
            body = await _http.GetStringAsync(
                $"cercaNumeroTrenoTrenoAutocomplete/{Uri.EscapeDataString(trainNumber)}", ct);
        }
        catch (HttpRequestException)
        {
            return [];
        }

        var result = new List<TrainRunRef>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = line.Split('|');
            if (halves.Length < 2) continue;

            var parts = halves[1].Trim().Split('-');
            if (parts.Length < 3) continue;

            result.Add(new TrainRunRef(parts[0], parts[1], parts[2], halves[0].Trim()));
        }
        return result;
    }

    /// <summary>
    /// ViaggiaTreno wants the timestamp spelled the way JavaScript's
    /// Date.toString() spells it, for example:
    ///
    ///     Mon Aug 10 2026 20:20:00 GMT+0200
    ///
    /// Day and month names must be English. They are built from fixed arrays
    /// on purpose: relying on the machine's culture makes this work on the
    /// developer's laptop and fail on an Italian-locale server.
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset when)
    {
        var rome = TimeZoneInfo.ConvertTime(when, RomeTz);
        var offset = rome.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();

        var day = Days[(int)rome.DayOfWeek];
        var month = Months[rome.Month - 1];

        return string.Create(CultureInfo.InvariantCulture,
            $"{day} {month} {rome.Day:00} {rome.Year} {rome:HH:mm:ss} " +
            $"GMT{sign}{abs.Hours:00}{abs.Minutes:00}");
    }

    /// <summary>
    /// Epoch milliseconds to Italian local time. The timezone is declared
    /// explicitly: converting with the machine's local time gives the right
    /// answer on an Italian laptop and the wrong one on a UTC server, which is
    /// the kind of bug that only appears once it is deployed.
    /// </summary>
    public static DateTimeOffset? ToRomeTime(long? epochMillis)
    {
        if (epochMillis is null or 0) return null;
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(epochMillis.Value);
        return TimeZoneInfo.ConvertTime(utc, RomeTz);
    }

    /// <summary>Formats an epoch value as "HH:mm", or "--:--" when missing.</summary>
    public static string ToRomeHhMm(long? epochMillis) =>
        ToRomeTime(epochMillis)?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "--:--";

    private static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] Months =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>
    /// ViaggiaTreno answers with an empty body instead of an empty array when
    /// there is nothing to return, which makes the JSON deserializer throw.
    /// </summary>
    private async Task<T?> SafeGetAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (body.Length == 0) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The three values andamentoTreno needs, plus a human-readable label.</summary>
public sealed record TrainRunRef(string Number, string OriginCode, string DepartureMillis, string Label);
