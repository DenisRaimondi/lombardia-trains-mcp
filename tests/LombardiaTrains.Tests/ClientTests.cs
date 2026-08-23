using System.Net;
using System.Net.Http.Headers;
using LombardiaTrains.Mcp.Clients;

namespace LombardiaTrains.Tests;

/// <summary>
/// These run against the live public endpoints on purpose. Mocking them would
/// only prove that the mocks match what we assumed, and every bug worth
/// catching here came from the real payload disagreeing with the assumption.
/// </summary>
public class ViaggiaTrenoTests
{
    private static ViaggiaTrenoClient NewClient() => new(new HttpClient());

    [Fact]
    public async Task SearchStation_finds_a_known_station()
    {
        var stations = await NewClient().SearchStationAsync("milano centrale");

        Assert.NotEmpty(stations);
        Assert.Contains(stations, s => s.LongName?.Contains("MILANO CENTRALE", StringComparison.OrdinalIgnoreCase) == true);
        Assert.All(stations, s => Assert.StartsWith("S", s.Id));
    }

    [Fact]
    public async Task Departures_from_a_major_station_are_returned()
    {
        var client = NewClient();
        var stations = await client.SearchStationAsync("milano centrale");
        var code = stations.First().Id!;

        var rows = await client.GetDeparturesAsync(code, DateTimeOffset.UtcNow);

        // Milano Centrale always has something scheduled, at any hour.
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.NotNull(r.TrainNumber));
    }

    [Fact]
    public async Task Unknown_station_returns_empty_not_throws()
    {
        var stations = await NewClient().SearchStationAsync("zzzznonexistent");
        Assert.Empty(stations);
    }

    /// <summary>
    /// The endpoint expects the timestamp spelled the way JavaScript writes it,
    /// with English day and month names. Building it from the machine culture
    /// would produce "lun ago" on an Italian box and silently return nothing.
    /// </summary>
    [Fact]
    public void Timestamp_uses_English_names_regardless_of_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("it-IT");

            // 10 August 2026 was a Monday; Italy is at UTC+2 in August.
            var when = new DateTimeOffset(2026, 8, 10, 20, 20, 0, TimeSpan.FromHours(2));

            Assert.Equal("Mon Aug 10 2026 20:20:00 GMT+0200",
                ViaggiaTrenoClient.FormatTimestamp(when));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// Epoch milliseconds must be converted declaring Europe/Rome. Using the
    /// machine's local time gives the right answer on an Italian laptop and the
    /// wrong one on a UTC server, so this asserts the absolute result.
    /// </summary>
    [Fact]
    public void Epoch_millis_convert_to_Italian_local_time()
    {
        // 2026-08-10T18:20:00Z is 20:20 in Rome (CEST, UTC+2).
        const long millis = 1786904400000;

        Assert.Equal("20:20", ViaggiaTrenoClient.ToRomeHhMm(millis));
        Assert.Null(ViaggiaTrenoClient.ToRomeTime(null));
        Assert.Null(ViaggiaTrenoClient.ToRomeTime(0));
    }
}

public class TrenordTests
{
    private const string ProbeUrl = "https://admin.trenord.it/store-management-api/mia/train/4307";

    private static async Task<HttpStatusCode> ProbeAsync(string? accept, string? userAgent)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Clear();
        if (accept is not null)
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (userAgent is not null)
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        using var response = await http.GetAsync(ProbeUrl);
        return response.StatusCode;
    }

    /// <summary>
    /// The behaviour the whole client is built around: Trenord wants BOTH an
    /// Accept header and a User-Agent, and refuses the request with 403 when
    /// either one is missing.
    ///
    /// This is easy to get half right. Testing from curl or Python suggests
    /// that Accept alone is enough, because both send a User-Agent of their own
    /// without being asked. .NET's HttpClient sends neither, so a client that
    /// only sets Accept still gets 403 — and only in production, which is the
    /// expensive place to find out.
    ///
    /// If this ever starts failing, the two headers in TrenordClient may no
    /// longer be needed, which is worth knowing.
    /// </summary>
    [Fact]
    public async Task Trenord_requires_both_Accept_and_UserAgent()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(accept: null, userAgent: null));
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(accept: "*/*", userAgent: null));
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(accept: null, userAgent: "probe/1.0"));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(accept: "*/*", userAgent: "probe/1.0"));
    }

    /// <summary>
    /// A train that is not running today yields an empty list rather than an
    /// error, so callers never have to distinguish "no service" from "failure".
    /// </summary>
    [Fact]
    public async Task Unknown_train_returns_empty_list()
    {
        var client = new TrenordClient(new HttpClient());
        var runs = await client.GetTrainAsync("999999");
        Assert.Empty(runs);
    }

    [Fact]
    public void Client_adds_the_Accept_header_itself()
    {
        var http = new HttpClient();
        _ = new TrenordClient(http);

        Assert.Contains(http.DefaultRequestHeaders.Accept, h => h.MediaType == "*/*");
    }
}
