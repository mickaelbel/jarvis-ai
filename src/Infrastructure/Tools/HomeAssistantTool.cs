using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using System.Net.Http.Json;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

// Domotique Home Assistant (API REST officielle + long-lived token).
// Complète Hue (pont direct) : entités de tous domaines, scènes, scripts.
// Actions : status · entites · lights · light · switch · scene · script · service.
public sealed class HomeAssistantTool : ITool
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly IntegrationsStore _store;

    public string Name => "homeassistant";
    public string Description =>
        "Domotique Home Assistant. Actions : status (API joignable), entites (liste des entités avec état, " +
        "paramètre domaine optionnel ex : light — À CONSULTER avant toute action pour trouver les bons entity_id), " +
        "lights (liste des lumières), light (on/off/luminosité : entity + on + brightness), switch (entity + on), " +
        "scene (entity = scene.xxx), script (entity = script.xxx), service (domaine + service + entity, appel générique).";
    public string Category => "home";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public bool McpExpose => true;
    public string WaitingPhrase => "Je pilote Home Assistant.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "status | entites | lights | light | switch | scene | script | service", typeof(string), required: true),
        new ToolParameter("entity", "Entity ID HA (ex : light.salon)", typeof(string)),
        new ToolParameter("on", "true ou false", typeof(string)),
        new ToolParameter("brightness", "Luminosité en % (1-100)", typeof(string)),
        new ToolParameter("domaine", "Filtre de domaine pour l'action entites (ex : light)", typeof(string)),
        new ToolParameter("domain", "Domaine (action service)", typeof(string)),
        new ToolParameter("service", "Nom du service (action service, ex : turn_on)", typeof(string))
    };

    public HomeAssistantTool(IntegrationsStore store) => _store = store;

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("entity", out var entity);
        var hasOn = parameters.TryGetValue("on", out var onRaw) && bool.TryParse(onRaw, out var on) ? on : (bool?)null;
        int? brightness = int.TryParse(parameters.GetValueOrDefault("brightness"), out var b) ? Math.Clamp(b, 1, 100) : null;
        parameters.TryGetValue("domain", out var domain);
        parameters.TryGetValue("service", out var service);

        var cfg = _store.Get().HomeAssistant;
        if (string.IsNullOrWhiteSpace(cfg.Url) || string.IsNullOrWhiteSpace(cfg.Token))
            return ToolResult.Failed("Home Assistant non configuré — renseigne l'URL et le jeton dans Paramètres → Intégrations.");

        var baseUrl = cfg.Url.TrimEnd('/');
        try
        {
            return action?.ToLowerInvariant() switch
            {
                "status" => await StatusAsync(baseUrl, cfg.Token, ct),
                "entites" => await EntitiesAsync(baseUrl, cfg.Token, parameters.GetValueOrDefault("domaine"), ct),
                "lights" => await LightsAsync(baseUrl, cfg.Token, ct),
                "light" when !string.IsNullOrWhiteSpace(entity) =>
                    await CallServiceAsync(baseUrl, cfg.Token, "light", hasOn == false ? "turn_off" : "turn_on",
                        entity, brightness, ct),
                "switch" when !string.IsNullOrWhiteSpace(entity) =>
                    await CallServiceAsync(baseUrl, cfg.Token, "switch", hasOn == false ? "turn_off" : "turn_on",
                        entity, null, ct),
                "scene" when !string.IsNullOrWhiteSpace(entity) =>
                    await CallServiceAsync(baseUrl, cfg.Token, "scene", "turn_on", entity, null, ct),
                "script" when !string.IsNullOrWhiteSpace(entity) =>
                    await CallServiceAsync(baseUrl, cfg.Token, "script", "turn_on", entity, null, ct),
                "service" when !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(service) =>
                    await CallServiceAsync(baseUrl, cfg.Token, domain!, service!, entity ?? "", null, ct),
                _ => ToolResult.Failed("Action inconnue ou paramètre manquant (entity requis).")
            };
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Failed($"Home Assistant injoignable ({ex.Message}) — vérifie l'URL et le réseau.");
        }
    }

    private static HttpRequestMessage ApiRequest(HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<ToolResult> StatusAsync(string baseUrl, string token, CancellationToken ct)
    {
        using var res = await Http.SendAsync(ApiRequest(HttpMethod.Get, $"{baseUrl}/api/", token), ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return ToolResult.Failed("Jeton refusé par Home Assistant (401).");
        res.EnsureSuccessStatusCode();
        return ToolResult.Succeeded("Home Assistant en ligne et jeton accepté.");
    }

    private static async Task<ToolResult> LightsAsync(string baseUrl, string token, CancellationToken ct)
    {
        using var res = await Http.SendAsync(ApiRequest(HttpMethod.Get, $"{baseUrl}/api/states", token), ct);
        res.EnsureSuccessStatusCode();
        var states = await res.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct);
        var lights = (states ?? [])
            .Where(s => s.GetProperty("entity_id").GetString()?.StartsWith("light.", StringComparison.Ordinal) == true)
            .Select(s =>
            {
                var attrs = s.GetProperty("attributes");
                var nom = attrs.TryGetProperty("friendly_name", out var f) ? f.GetString() : "";
                var allume = s.GetProperty("state").GetString() == "on";
                return $"  • {s.GetProperty("entity_id").GetString()} — {nom} ({(allume ? "allumé" : "éteint")})";
            })
            .ToList();
        return lights.Count == 0
            ? ToolResult.Succeeded("Aucune lumière trouvée dans Home Assistant.")
            : ToolResult.Succeeded($"{lights.Count} lumière(s) :\n" + string.Join("\n", lights));
    }

    private static async Task<ToolResult> EntitiesAsync(string baseUrl, string token, string? domainFilter, CancellationToken ct)
    {
        using var res = await Http.SendAsync(ApiRequest(HttpMethod.Get, $"{baseUrl}/api/states", token), ct);
        res.EnsureSuccessStatusCode();
        var states = await res.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct) ?? [];
        var filter = domainFilter?.Trim().ToLowerInvariant();

        var sb = new System.Text.StringBuilder();
        var shown = 0;
        foreach (var s in states)
        {
            var entityId = s.TryGetProperty("entity_id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(entityId)) continue;
            if (!string.IsNullOrEmpty(filter) && !entityId.StartsWith(filter + ".", StringComparison.Ordinal)) continue;

            var state = s.TryGetProperty("state", out var stEl) ? stEl.GetString() : "?";
            var name = "";
            if (s.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("friendly_name", out var fn))
                name = fn.GetString() ?? "";
            if (name == entityId) name = "";

            sb.Append($"• {entityId}");
            if (name.Length > 0) sb.Append($" ({name})");
            sb.AppendLine($" = {state}");
            if (++shown >= 80) break;
        }
        if (shown == 0)
            return ToolResult.Failed(string.IsNullOrEmpty(filter)
                ? "Aucune entité trouvée dans Home Assistant."
                : $"Aucune entité du domaine « {filter} » trouvée.");
        var total = string.IsNullOrEmpty(filter) ? states.Length : shown;
        return ToolResult.Succeeded(
            $"ENTITÉS HOME ASSISTANT ({(total > shown ? $"{shown} premières sur {total}" : total)}) :\n{sb}" +
            (total > shown ? "\n(précise le paramètre domaine pour affiner)" : ""));
    }

    private static async Task<ToolResult> CallServiceAsync(string baseUrl, string token,
        string domaine, string service, string entity, int? brightnessPct, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(entity)) payload["entity_id"] = entity;
        if (brightnessPct.HasValue) payload["brightness_pct"] = brightnessPct.Value;

        using var res = await Http.SendAsync(
            ApiRequest(HttpMethod.Post, $"{baseUrl}/api/services/{domaine}/{service}", token, payload), ct);
        res.EnsureSuccessStatusCode();

        var details = brightnessPct.HasValue ? $" à {brightnessPct} %" : "";
        return ToolResult.Succeeded($"{domaine}.{service} {entity}{details} — fait.");
    }
}