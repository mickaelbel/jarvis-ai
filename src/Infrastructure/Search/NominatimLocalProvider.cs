using System.Globalization;
using System.Text.Json;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class NominatimLocalProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<NominatimLocalProvider> _logger;

    public NominatimLocalProvider(HttpClient http, ILogger<NominatimLocalProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "nominatim";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.Local;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var parsed = LocalQueryParser.Parse(request.Query);
        var searchTerms = string.IsNullOrWhiteSpace(parsed.CleanQuery) ? request.Query : parsed.CleanQuery;

        double? nearLat = request.NearLatitude;
        double? nearLon = request.NearLongitude;

        if (nearLat is null && !string.IsNullOrWhiteSpace(parsed.City))
        {
            var geo = await GeocodeAsync(parsed.City, cancellationToken);
            if (geo is not null)
            {
                nearLat = geo.Value.Latitude;
                nearLon = geo.Value.Longitude;
            }
        }

        var encoded = Uri.EscapeDataString(searchTerms);
        var url = $"https://nominatim.openstreetmap.org/search?format=json&limit={request.MaxResults}&addressdetails=1&extratags=1&q={encoded}";

        var json = await _http.GetStringAsync(url, cancellationToken);
        var openNowOnly = parsed.OpenNow ? true : (bool?)null;
        var results = ParseResults(json, request.MaxResults, nearLat, nearLon, openNowOnly);
        _logger.LogDebug("[Nominatim] {Query}: {Count} results", request.Query, results.Count);
        return results;
    }

    internal async Task<(double Latitude, double Longitude)?> GeocodeAsync(string city, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(city);
        var url = $"https://nominatim.openstreetmap.org/search?format=json&limit=1&q={encoded}";

        var json = await _http.GetStringAsync(url, cancellationToken);
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var item = doc.RootElement[0];
        if (double.TryParse(GetString(item, "lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
            double.TryParse(GetString(item, "lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return (lat, lon);
        return null;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string json, int maxResults, double? nearLat = null, double? nearLon = null, bool? openNowOnly = null)
    {
        var results = new List<SearchResult>();
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null)
            return results;

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            var displayName = GetString(item, "display_name") ?? string.Empty;
            var name = GetString(item, "name");
            var type = GetString(item, "type");
            var category = GetString(item, "class");
            var lat = TryDouble(item, "lat");
            var lon = TryDouble(item, "lon");

            if (string.IsNullOrWhiteSpace(displayName) || lat is null || lon is null)
                continue;

            string? website = null;
            string? openingHours = null;
            if (item.TryGetProperty("extratags", out var extras))
            {
                website = GetString(extras, "website");
                openingHours = GetString(extras, "opening_hours") ?? GetString(extras, "opening_hours:24/7");
            }

            var openNow = OpeningHours.IsOpenNow(openingHours);
            if (openNowOnly == true && openNow != true)
                continue;

            double? distance = nearLat is not null && nearLon is not null ? Haversine(nearLat.Value, nearLon.Value, lat.Value, lon.Value) : (double?)null;

            var snippet = type ?? category ?? "Lieu";
            if (distance is { } d)
                snippet += $" · à {d:F1} km";
            if (openNow is { } o)
                snippet += o ? " · Ouvert" : " · Fermé";
            if (website is not null)
                snippet += $" · {website}";

            var extraScore = ComputeExtraScore(distance, website, openNow);

            results.Add(new SearchResult
            {
                Title = name ?? displayName,
                Url = $"https://www.openstreetmap.org/?mlat={lat.Value.ToString(CultureInfo.InvariantCulture)}&mlon={lon.Value.ToString(CultureInfo.InvariantCulture)}",
                Snippet = snippet,
                Provider = "nominatim",
                Source = "openstreetmap.org",
                Type = SearchResultType.Local,
                OpenNow = openNow,
                ExtraScore = extraScore,
                Latitude = lat,
                Longitude = lon
            });
        }

        return results.OrderByDescending(r => r.ExtraScore ?? 0).ToList();
    }

    internal static double ComputeExtraScore(double? distanceKm, string? website, bool? openNow)
    {
        var score = 0.0;
        if (distanceKm is { } d)
            score += Math.Clamp(1.0 - d / 25.0, 0.0, 1.0) * 0.6;
        if (!string.IsNullOrWhiteSpace(website))
            score += 0.2;
        if (openNow == true)
            score += 0.2;
        return Math.Clamp(score, 0.0, 1.0);
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return r * c;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? TryDouble(JsonElement element, string property)
        => double.TryParse(GetString(element, property), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
