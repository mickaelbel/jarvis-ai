using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class NestTool : ITool
{
    private readonly GoogleAuthHelper _auth;
    private readonly IntegrationsStore _store;
    private readonly ILogger<NestTool> _logger;
    private readonly HttpClient _http;

    public NestTool(GoogleAuthHelper auth, IntegrationsStore store, ILogger<NestTool> logger)
    {
        _auth = auth;
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "nest";
    public string Description =>
        "Google Home / Nest (EXPÉRIMENTAL) : liste appareils et état (connectivité, température). " +
        "Action : status. Basé sur Smart Device Management API — pas de contrôle natif allumage/luminosité tiers.";
    public string Category => "home";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "J'interroge Nest…";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "status", typeof(string), required: true)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync(ct);
            var settings = _store.Get().Nest;
            if (string.IsNullOrWhiteSpace(settings.ProjectId))
                return ToolResult.Failed("Nest non configuré : ProjectId requis dans les paramètres.");

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://smartdevicemanagement.googleapis.com/v1/enterprises/{Uri.EscapeDataString(settings.ProjectId)}/devices");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return ToolResult.Failed($"Nest API error ({resp.StatusCode}) : {err}");
            }
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("devices", out var devices))
                return ToolResult.Succeeded("Aucun appareil Nest trouvé.");

            var sb = new System.Text.StringBuilder("APPAREILS NEST :\n");
            foreach (var d in devices.EnumerateArray())
            {
                var name = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var type = name.Split('/').LastOrDefault() ?? name;
                var traits = d.TryGetProperty("traits", out var t) ? t : default;
                sb.AppendLine($"  • {type}");
                if (traits.ValueKind != JsonValueKind.Undefined)
                {
                    if (traits.TryGetProperty("sdm.devices.traits.Connectivity", out var conn) &&
                        conn.TryGetProperty("status", out var cs))
                        sb.AppendLine($"      Connectivité : {cs.GetString()}");
                    if (traits.TryGetProperty("sdm.devices.traits.Temperature", out var temp) &&
                        temp.TryGetProperty("ambientTemperatureCelsius", out var tc))
                        sb.AppendLine($"      Température : {tc.GetDouble():0.#} °C");
                    // TODO: autres traits si besoin
                }
            }
            return ToolResult.Succeeded(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Nest] status échoué");
            return ToolResult.Failed($"Erreur Nest : {ex.Message}");
        }
    }
}