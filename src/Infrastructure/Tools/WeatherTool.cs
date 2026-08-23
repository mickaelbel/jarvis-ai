using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class WeatherTool : ITool
{
    private readonly HttpClient _http;

    public WeatherTool(HttpClient http)
    {
        _http = http;
    }

    public string Name => "weather";
    public string Description =>
        "Returns the current weather and a 3-day forecast for a city or place (uses Open-Meteo, no API key). " +
        "Temperature, conditions, wind, precipitation. Usage: weather(location: \"Paris\").";
    public string Category => "web";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public bool McpExpose => true;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("location", "City or place name (e.g. 'Paris', 'New York')", typeof(string), required: true)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!parameters.TryGetValue("location", out var location) || string.IsNullOrWhiteSpace(location))
            {
                return ToolResult.Failed("Missing required parameter 'location'.");
            }

            var (lat, lon, name) = await GeocodeAsync(location.Trim(), cancellationToken);
            if (lat is null)
            {
                return ToolResult.Failed($"Lieu introuvable : {location}");
            }

            var forecastUrl =
                $"https://api.open-meteo.com/v1/forecast?latitude={lat.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" +
                $"&longitude={lon!.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" +
                $"&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,wind_speed_10m" +
                $"&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max,weather_code" +
                $"&timezone=auto&forecast_days=3";

            using var doc = await JsonDocument.ParseAsync(await _http.GetStreamAsync(forecastUrl, cancellationToken), cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var current = root.GetProperty("current");
            var code = current.GetProperty("weather_code").GetInt32();
            var temp = current.GetProperty("temperature_2m").GetDouble();
            var feels = current.GetProperty("apparent_temperature").GetDouble();
            var humidity = current.GetProperty("relative_humidity_2m").GetInt32();
            var wind = current.GetProperty("wind_speed_10m").GetDouble();
            var precip = current.GetProperty("precipitation").GetDouble();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Météo actuelle à {name} :");
            sb.AppendLine($"- Conditions : {WeatherCodeFr(code)}");
            sb.AppendLine($"- Température : {temp:0.#} °C (ressenti {feels:0.#} °C)");
            sb.AppendLine($"- Humidité : {humidity} % | Vent : {wind:0.#} km/h");
            if (precip > 0) sb.AppendLine($"- Précipitations : {precip:0.#} mm");

            if (root.TryGetProperty("daily", out var daily))
            {
                var days = daily.GetProperty("time");
                var tMax = daily.GetProperty("temperature_2m_max");
                var tMin = daily.GetProperty("temperature_2m_min");
                var pop = daily.GetProperty("precipitation_probability_max");
                var codes = daily.GetProperty("weather_code");

                sb.AppendLine();
                sb.AppendLine("Prévisions 3 jours :");
                for (var i = 0; i < days.GetArrayLength(); i++)
                {
                    var label = i == 0 ? "Aujourd'hui" : i == 1 ? "Demain" : days[i].GetString();
                    sb.AppendLine($"- {label} : {WeatherCodeFr(codes[i].GetInt32())}, {tMin[i].GetDouble():0.#}/{tMax[i].GetDouble():0.#} °C, pluie {pop[i].GetInt32()} %");
                }
            }

            return ToolResult.Succeeded(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return ToolResult.Failed("Erreur météo : " + ex.Message);
        }
    }

    private async Task<(double? Lat, double? Lon, string Name)> GeocodeAsync(string location, CancellationToken ct)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(location)}&count=1&language=fr&format=json";
        using var doc = await JsonDocument.ParseAsync(await _http.GetStreamAsync(url, ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return (null, null, location);
        }
        var first = results[0];
        return (
            first.GetProperty("latitude").GetDouble(),
            first.GetProperty("longitude").GetDouble(),
            first.TryGetProperty("name", out var n) ? n.GetString() ?? location : location);
    }

    private static string WeatherCodeFr(int code) => code switch
    {
        0 => "ciel dégagé",
        1 => "plutôt dégagé",
        2 => "partiellement nuageux",
        3 => "couvert",
        45 or 48 => "brouillard",
        51 or 53 or 55 => "bruine",
        56 or 57 => "bruine verglaçante",
        61 or 63 or 65 => "pluie",
        66 or 67 => "pluie verglaçante",
        71 or 73 or 75 => "neige",
        77 => "grains de neige",
        80 or 81 or 82 => "averses",
        85 or 86 => "averses de neige",
        95 => "orage",
        96 or 99 => "orage avec grêle",
        _ => "conditions variables"
    };
}

