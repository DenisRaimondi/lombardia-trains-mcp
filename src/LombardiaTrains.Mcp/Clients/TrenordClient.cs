using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LombardiaTrains.Mcp.Models;

namespace LombardiaTrains.Mcp.Clients;

/// <summary>
/// Client for Trenord's public store-management-api. No authentication.
///
/// One thing will bite you here and it is worth stating up front: the API
/// answers 403 Forbidden unless the request carries BOTH an Accept header and
/// a User-Agent. Measured against the live endpoint:
///
///     no headers at all         -> 403
///     Accept: */* only          -> 403
///     User-Agent only           -> 403
///     Accept + User-Agent       -> 200
///
/// The trap is that this looks like an Accept-only rule when you probe it from
/// curl or Python, because both send a User-Agent of their own without being
/// asked. .NET's HttpClient sends neither header unless told to, so a client
/// that sets only Accept keeps failing — and it fails in production, never on
/// the machine where the request was first tried by hand.
/// </summary>
public sealed class TrenordClient
{
    private const string BaseUrl = "https://admin.trenord.it/store-management-api/mia";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public TrenordClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(BaseUrl + "/");

        // The line this whole client depends on.
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("lombardia-trains-mcp/1.0");
    }

    /// <summary>
    /// Every run scheduled today for this train number. The result is a list
    /// because the same number can be used by more than one run in a day.
    /// Returns an empty list when the train does not run today.
    /// </summary>
    public async Task<IReadOnlyList<TrenordRun>> GetTrainAsync(
        string trainNumber, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"train/{Uri.EscapeDataString(trainNumber)}", ct);

        if (!response.IsSuccessStatusCode)
            return [];

        var runs = await response.Content.ReadFromJsonAsync<List<TrenordRun>>(JsonOptions, ct);
        return runs ?? [];
    }
}
