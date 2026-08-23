using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LombardiaTrains.Mcp.Clients;

/// <summary>
/// Client for transport.opendata.ch, the Swiss public transport API.
///
/// It is here for the one thing neither Italian source can do: plan a journey
/// that needs a change, and continue past the border. Its coverage reaches into
/// Italy far enough to know Milano, Malpensa and the Lombardy stations on the
/// cross-border lines, so a route from Lombardy into Ticino can be planned end
/// to end.
///
/// It is deliberately not used for anything else. On Italian stations it only
/// sees the trains its own network runs across the border, reports no delay and
/// no platform, and misses the regional traffic entirely — which is exactly
/// what ViaggiaTreno and Trenord are good at. Planning comes from here; what is
/// actually happening comes from them.
///
/// Chosen over Trenitalia's own planner on purpose. That one answers from the
/// booking backend behind lefrecce.it — a request to it opens a shopping cart —
/// with no published terms for third-party use. This API states on its home
/// page that it exists for developers to build on.
/// </summary>
public sealed class SwissTransportClient
{
    private const string BaseUrl = "https://transport.opendata.ch/v1";

    private readonly HttpClient _http;

    public SwissTransportClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(BaseUrl + "/");
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("lombardia-trains-mcp/1.0");
    }

    /// <summary>
    /// Journeys between two places, changes included. Times are local to each
    /// stop and come back as ISO 8601 with an offset.
    /// </summary>
    public async Task<IReadOnlyList<SwissConnection>> GetConnectionsAsync(
        string from, string to, DateTimeOffset when, int limit = 4, CancellationToken ct = default)
    {
        var url = $"connections?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}" +
                  $"&date={when:yyyy-MM-dd}&time={when:HH\\:mm}&limit={Math.Clamp(limit, 1, 6)}";

        var result = await SafeGetAsync<SwissConnectionsResponse>(url, ct);
        return result?.Connections ?? [];
    }

    private async Task<T?> SafeGetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (HttpRequestException)
        {
            // The service being unreachable is an expected condition; a payload
            // we cannot parse is a bug, and is deliberately left to surface.
            return null;
        }
    }
}

public sealed class SwissConnectionsResponse
{
    [JsonPropertyName("connections")] public List<SwissConnection> Connections { get; init; } = [];
}

public sealed class SwissConnection
{
    [JsonPropertyName("from")] public SwissCheckpoint? From { get; init; }
    [JsonPropertyName("to")] public SwissCheckpoint? To { get; init; }

    /// <summary>Formatted by the API as "00d01:14:00".</summary>
    [JsonPropertyName("duration")] public string? Duration { get; init; }

    [JsonPropertyName("transfers")] public int? Transfers { get; init; }
    [JsonPropertyName("sections")] public List<SwissSection> Sections { get; init; } = [];

    /// <summary>Turns "00d01:14:00" into minutes; null when unparseable.</summary>
    [JsonIgnore]
    public int? DurationMinutes
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Duration)) return null;
            var clock = Duration.Contains('d') ? Duration[(Duration.IndexOf('d') + 1)..] : Duration;
            var days = 0;
            if (Duration.Contains('d') && int.TryParse(Duration[..Duration.IndexOf('d')], out var d)) days = d;
            return TimeSpan.TryParse(clock, CultureInfo.InvariantCulture, out var ts)
                ? (int)(ts.TotalMinutes + days * 1440)
                : null;
        }
    }
}

public sealed class SwissSection
{
    [JsonPropertyName("departure")] public SwissCheckpoint? Departure { get; init; }
    [JsonPropertyName("arrival")] public SwissCheckpoint? Arrival { get; init; }
    [JsonPropertyName("journey")] public SwissJourney? Journey { get; init; }

    /// <summary>A section without a journey is a walk between two stops.</summary>
    [JsonIgnore] public bool IsWalk => Journey is null;
}

public sealed class SwissJourney
{
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("number")] public string? Number { get; init; }
    [JsonPropertyName("operator")] public string? Operator { get; init; }
    [JsonPropertyName("to")] public string? Destination { get; init; }

    /// <summary>
    /// The line as printed on the train: category and number run together, so
    /// category "S" with number "50" is the S50, not "S 50".
    /// </summary>
    [JsonIgnore]
    public string Label => string.Concat(Category, Number);
}

public sealed class SwissCheckpoint
{
    [JsonPropertyName("station")] public SwissStation? Station { get; init; }
    [JsonPropertyName("departure")]
    [JsonConverter(typeof(FlexibleDateTimeOffsetConverter))]
    public DateTimeOffset? Departure { get; init; }

    [JsonPropertyName("arrival")]
    [JsonConverter(typeof(FlexibleDateTimeOffsetConverter))]
    public DateTimeOffset? Arrival { get; init; }
    [JsonPropertyName("platform")] public string? Platform { get; init; }

    /// <summary>Minutes late. Null on Italian stops: this API does not track them.</summary>
    [JsonPropertyName("delay")] public int? Delay { get; init; }

    [JsonIgnore] public string Name => Station?.Name ?? "?";
}

public sealed class SwissStation
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}
