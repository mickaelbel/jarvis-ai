using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class AlexaTool : ITool
{
    private readonly IntegrationsStore _store;
    private readonly ILogger<AlexaTool> _logger;
    private readonly HttpClient _http;

    public AlexaTool(IntegrationsStore store, ILogger<AlexaTool> logger)
    {
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "alexa";
    public string Description =>
        "Alexa / Echo (EXPÉRIMENTAL — API non officielle) : annonces TTS, média, utterance routine. " +
        "Nécessite AccessToken (Bearer) collé depuis .storage/alexa_media.<email>.oauth.json. " +
        "Actions : status, announce, media, routine. Announce = N1, Routine = N2.";
    public string Category => "home";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "status | announce | media | routine", typeof(string), required: true),
        new("text", "Texte à annoncer (announce)", typeof(string)),
        new("command", "Commande média : play | pause | next | previous | volume_up | volume_down | set_volume:N (media)", typeof(string)),
        new("utterance", "Phrase routine complète ex: \"allume la clim\" (routine)", typeof(string)),
        new("device", "Serial number cible (optionnel, défaut = tous)", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        var settings = _store.Get().Alexa;
        if (string.IsNullOrWhiteSpace(settings.AccessToken))
            return ToolResult.Failed("Alexa non configuré : AccessToken requis (copie depuis .storage/alexa_media.<email>.oauth.json).");

        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "status" => StatusAsync(settings, ct),
                "announce" => AnnounceAsync(settings, parameters, ct),
                "media" => MediaAsync(settings, parameters, ct),
                "routine" => RoutineAsync(settings, parameters, ct),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : status, announce, media, routine"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Alexa] {Action} échoué", action);
            return ToolResult.Failed($"Erreur Alexa : {ex.Message}");
        }
    }

    private async Task<ToolResult> StatusAsync(AlexaSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.AccessToken))
            return ToolResult.Failed("AccessToken manquant.");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.amazonalexa.com/v1/devices");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", s.AccessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            // try refresh if 401
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(s.RefreshToken))
                return await TryRefreshAndRetryAsync(s, ct);
            return ToolResult.Failed($"Alexa status failed ({resp.StatusCode}) : {err}");
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var sb = new System.Text.StringBuilder("APPAREILS ALEXA :\n");
        foreach (var d in doc.RootElement.EnumerateArray())
        {
            var name = d.TryGetProperty("accountName", out var n) ? n.GetString() : d.TryGetProperty("deviceType", out var t) ? t.GetString() : "";
            var sn = d.TryGetProperty("serialNumber", out var snp) ? snp.GetString() : "";
            var online = d.TryGetProperty("online", out var o) && o.GetBoolean();
            sb.AppendLine($"  • {name} ({sn}) : {(online ? "en ligne" : "hors ligne")}");
        }
        return ToolResult.Succeeded(sb.ToString());
    }

    private async Task<ToolResult> TryRefreshAndRetryAsync(AlexaSettings s, CancellationToken ct)
    {
        // OAuth refresh against amazon
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = s.RefreshToken,
            ["client_id"] = "amzn1.application-oa2-client.xxx" // placeholder; real client_id needed
        };
        // This needs proper LWA client_id/secret - skipped for now
        return ToolResult.Failed("Token expiré — refresh non implémenté (nécessite client_id LWA). Colle un AccessToken frais dans les paramètres.");
    }

    private async Task<ToolResult> AnnounceAsync(AlexaSettings s, IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var text = p.GetValueOrDefault("text");
        if (string.IsNullOrWhiteSpace(text)) return ToolResult.Failed("Paramètre text requis.");

        // Payload behaviors/preview (structure exacte du repo Python alexapy).
        // Les clés "@type" interdisent les types anonymes C# : on passe par un dictionnaire.
        var sequence = new Dictionary<string, object?>
        {
            ["@type"] = "com.amazon.alexa.behaviors.model.Sequence",
            ["startNode"] = new Dictionary<string, object?>
            {
                ["@type"] = "com.amazon.alexa.behaviors.model.OpaquePayloadOperationNode",
                ["type"] = "Alexa.Speak",
                ["operationPayload"] = new Dictionary<string, object?>
                {
                    ["deviceSerialNumber"] = s.SerialNumber,
                    ["locale"] = "fr-FR",
                    ["textToSpeak"] = text
                }
            }
        };
        var payload = JsonSerializer.Serialize(new
        {
            behaviorId = "PREVIEW",
            type = "Alexa.Web",
            sequenceJson = JsonSerializer.Serialize(sequence)
        });
        _logger.LogInformation("[Alexa] Annonce préparée ({Chars} chars de payload)", payload.Length);
        return ToolResult.Succeeded($"[EXPÉRIMENTAL] Annonce préparée pour « {text} » — endpoint /api/behaviors/preview à valider selon ton setup Alexa (cookies vs Bearer).");
    }

    private async Task<ToolResult> MediaAsync(AlexaSettings s, IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var cmd = p.GetValueOrDefault("command")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cmd)) return ToolResult.Failed("Paramètre command requis (play/pause/next/previous/volume_up/volume_down/set_volume:N).");
        // Similaire à announce : endpoint /v2/behaviors/preview avec payload media
        return ToolResult.Succeeded($"[EXPÉRIMENTAL] Commande média '{cmd}' — endpoint à valider.");
    }

    private async Task<ToolResult> RoutineAsync(AlexaSettings s, IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var utterance = p.GetValueOrDefault("utterance");
        if (string.IsNullOrWhiteSpace(utterance)) return ToolResult.Failed("Paramètre utterance requis (ex: \"allume la clim\").");
        // Execute routine via utterance — préférable via routineId connue; ici squelette
        return ToolResult.Succeeded($"[EXPÉRIMENTAL] Routine « {utterance} » — déclenchement via utterance non standard. Utilise routineId si dispo.");
    }
}




