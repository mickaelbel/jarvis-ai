using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Hue;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Domotique Philips Hue : lumières (on/off, luminosité, couleur), groupes,
/// scènes, appairage du pont. Portage de tools/lumieres.py du repo Python.
/// </summary>
public sealed class HueTool : ITool
{
    private readonly HueBridgeClient _bridge;
    private readonly ILogger<HueTool> _logger;

    public string Name => "hue";
    public string Description =>
        "Domotique Philips Hue. Actions : status (lumières et groupes), discover (trouve le pont sur le réseau), " +
        "pair (appairage — appuie d'abord sur le bouton du pont), light (allume/éteint/éclaire une lumière : id + on + brightness + color), " +
        "group (même chose pour une pièce entière), scene (active une ambiance : scenes pour lister, puis scene avec id), scenes (liste des ambiances). " +
        "Couleurs nommées acceptées : rouge, vert, bleu, blanc, orange, jaune, rose, violet.";
    public string Category => "home";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public bool McpExpose => true;
    public string WaitingPhrase => "Je pilote les lumières.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "status | discover | pair | light | group | scene | scenes", typeof(string), required: true),
        new ToolParameter("id", "Identifiant de la lumière/groupe/scène", typeof(string)),
        new ToolParameter("on", "true ou false", typeof(string)),
        new ToolParameter("brightness", "Luminosité en % (1-100)", typeof(string)),
        new ToolParameter("color", "Couleur : rouge, vert, bleu, blanc, orange, jaune, rose, violet", typeof(string))
    };

    public HueTool(HueBridgeClient bridge, ILogger<HueTool> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("id", out var id);
        var hasOn = parameters.TryGetValue("on", out var onRaw);
        int? brightness = int.TryParse(parameters.GetValueOrDefault("brightness"), out var b) ? b : null;
        parameters.TryGetValue("color", out var color);

        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "discover" => DiscoverAsync(cancellationToken),
                "pair" => PairAsync(cancellationToken),
                "status" => StatusAsync(cancellationToken),
                "scenes" => ScenesAsync(cancellationToken),
                "light" => LightAsync(id, hasOn, onRaw, brightness, color, cancellationToken),
                "group" => GroupAsync(id, hasOn, onRaw, brightness, color, cancellationToken),
                "scene" => SceneAsync(id, cancellationToken),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : status, discover, pair, light, group, scene, scenes"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hue] Action {Action} échouée", action);
            return ToolResult.Failed($"Erreur Hue : {ex.Message}");
        }
    }

    private async Task<ToolResult> DiscoverAsync(CancellationToken ct)
    {
        var ip = await _bridge.DiscoverAsync(ct);
        return ip is null
            ? ToolResult.Failed("Aucun pont Hue trouvé sur le réseau (vérifie qu'il est allumé et sur le même réseau).")
            : ToolResult.Succeeded($"Pont Hue trouvé à l'adresse {ip}. Configure JarvisAI:Hue:BridgeIp=« {ip} » dans appsettings.json.");
    }

    private async Task<ToolResult> PairAsync(CancellationToken ct)
    {
        try
        {
            var key = await _bridge.PairAsync(null, ct);
            return key.Length > 0
                ? ToolResult.Succeeded($"Appairage réussi ! Copie cette clé dans appsettings.json (JarvisAI:Hue:AppKey) puis redémarre Jarvis : {key}")
                : ToolResult.Failed("Appairage sans réponse du pont.");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Failed(ex.Message);
        }
    }

    private async Task<ToolResult> StatusAsync(CancellationToken ct)
    {
        if (!_bridge.IsConfigured)
            return ToolResult.Failed("Hue n'est pas appairé. Dis « hue pair » après avoir appuyé sur le bouton du pont.");

        var lights = await _bridge.ListLightsAsync(ct);
        var groups = await _bridge.ListGroupsAsync(ct);
        var sb = new System.Text.StringBuilder("LUMIÈRES :\n");
        foreach (var l in lights.Take(20))
            sb.AppendLine($"  [{l.Id}] {l.Name} — {(l.On ? $"ALLUMÉE ({l.Bri * 100 / 254}%)" : "éteinte")}");
        sb.AppendLine("PIÈCES (groupes) :");
        foreach (var g in groups.Where(g => g.Name != "Group 0").Take(15))
            sb.AppendLine($"  [{g.Id}] {g.Name} — {(g.AnyOn ? "allumée" : "éteinte")}");
        return ToolResult.Succeeded(sb.ToString());
    }

    private async Task<ToolResult> ScenesAsync(CancellationToken ct)
    {
        if (!_bridge.IsConfigured)
            return ToolResult.Failed("Hue n'est pas appairé.");
        var scenes = await _bridge.ListScenesAsync(ct);
        var lines = scenes.Take(30).Select(s => $"  [{s.Id}] {s.Name}");
        return ToolResult.Succeeded("AMBIANCES/SCÈNES :\n" + string.Join("\n", lines));
    }

    private async Task<ToolResult> LightAsync(string? id, bool hasOn, string? onRaw, int? brightness, string? color, CancellationToken ct)
    {
        if (!_bridge.IsConfigured) return ToolResult.Failed("Hue n'est pas appairé.");
        if (string.IsNullOrWhiteSpace(id)) return ToolResult.Failed("Paramètre 'id' requis (utilise action=status pour lister).");
        await _bridge.SetLightAsync(id, ParseOn(hasOn, onRaw), brightness, color, ct);
        return ToolResult.Succeeded("Lumière mise à jour. ACTION TERMINÉE.");
    }

    private async Task<ToolResult> GroupAsync(string? id, bool hasOn, string? onRaw, int? brightness, string? color, CancellationToken ct)
    {
        if (!_bridge.IsConfigured) return ToolResult.Failed("Hue n'est pas appairé.");
        if (string.IsNullOrWhiteSpace(id)) return ToolResult.Failed("Paramètre 'id' requis (utilise action=status pour lister).");
        await _bridge.SetGroupAsync(id, ParseOn(hasOn, onRaw), brightness, color, ct);
        return ToolResult.Succeeded("Groupe mis à jour. ACTION TERMINÉE.");
    }

    private async Task<ToolResult> SceneAsync(string? id, CancellationToken ct)
    {
        if (!_bridge.IsConfigured) return ToolResult.Failed("Hue n'est pas appairé.");
        if (string.IsNullOrWhiteSpace(id)) return ToolResult.Failed("Paramètre 'id' requis (utilise action=scenes pour lister).");
        var groups = await _bridge.ListGroupsAsync(ct);
        var home = groups.FirstOrDefault(g => g.Name == "Group 0").Id
                   ?? groups.FirstOrDefault().Id
                   ?? throw new InvalidOperationException("Aucun groupe disponible pour appliquer la scène.");
        await _bridge.ActivateSceneAsync(id, home, ct);
        return ToolResult.Succeeded("Ambiance activée. ACTION TERMINÉE.");
    }

    private static bool? ParseOn(bool hasOn, string? raw) =>
        !hasOn ? null : raw is not null && (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1");
}


