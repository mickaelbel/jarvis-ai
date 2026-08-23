using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Obs;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Contrôle d'OBS Studio via obs-websocket v5 : direct, enregistrement,
/// scènes, replay buffer. Portage de tools/obs.py du repo Python.
/// </summary>
public sealed class ObsTool : ITool
{
    private readonly ObsWebSocketClient _client;
    private readonly ObsOptions _options;
    private readonly ILogger<ObsTool> _logger;

    public string Name => "obs";
    public string Description =>
        "Contrôle d'OBS Studio (streaming). Actions : status (état direct/enregistrement + scène courante), scenes (liste), set_scene (change de scène), " +
        "start_stream, stop_stream, start_record, stop_record, replay (sauve un replay des X dernières secondes). " +
        "Pour « lance le direct », « coupe l'enregistrement », « passe sur la scène gameplay ».";
    public string Category => "streaming";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public bool McpExpose => true;
    public string WaitingPhrase => "Je pilote OBS.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "status | scenes | set_scene | start_stream | stop_stream | start_record | stop_record | replay", typeof(string), required: true),
        new ToolParameter("name", "Nom de la scène cible (set_scene)", typeof(string))
    };

    public ObsTool(ObsWebSocketClient client, ObsOptions options, ILogger<ObsTool> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("name", out var sceneName);
        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "status" => StatusAsync(cancellationToken),
                "scenes" => ScenesAsync(cancellationToken),
                "set_scene" => SetSceneAsync(sceneName, cancellationToken),
                "start_stream" => SimpleAsync("StartStream", "Direct lancé."),
                "stop_stream" => SimpleAsync("StopStream", "Direct arrêté."),
                "start_record" => SimpleAsync("StartRecord", "Enregistrement lancé."),
                "stop_record" => SimpleAsync("StopRecord", "Enregistrement arrêté."),
                "replay" => SimpleAsync("SaveReplayBuffer", "Replay sauvegardé."),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : status, scenes, set_scene, start_stream, stop_stream, start_record, stop_record, replay"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OBS] Action {Action} échouée", action);
            return ToolResult.Failed(
                $"OBS indisponible ({ex.Message}). Vérifie qu'OBS est ouvert et que le serveur WebSocket est activé (Outils → WebSocket Server Settings).");
        }
    }

    private async Task<ToolResult> StatusAsync(CancellationToken ct)
    {
        var stream = await _client.CallAsync("GetStreamStatus", null, ct);
        var record = await _client.CallAsync("GetRecordStatus", null, ct);
        var scene = await _client.CallAsync("GetCurrentProgramScene", null, ct);
        var streaming = stream.TryGetProperty("outputActive", out var sa) && sa.GetBoolean();
        var recording = record.TryGetProperty("outputActive", out var ra) && ra.GetBoolean();
        var currentScene = scene.TryGetProperty("sceneName", out var sn) ? sn.GetString() : "?";
        return ToolResult.Succeeded($"OBS — Direct : {(streaming ? "EN COURS" : "arrêté")} | Enregistrement : {(recording ? "EN COURS" : "arrêté")} | Scène : {currentScene}");
    }

    private async Task<ToolResult> ScenesAsync(CancellationToken ct)
    {
        var res = await _client.CallAsync("GetSceneList", null, ct);
        if (!res.TryGetProperty("scenes", out var scenes))
            return ToolResult.Failed("Liste de scènes introuvable.");
        var names = scenes.EnumerateArray()
            .Select(s => s.TryGetProperty("sceneName", out var n) ? n.GetString() : "")
            .Where(n => !string.IsNullOrEmpty(n));
        return ToolResult.Succeeded("Scènes OBS :\n  " + string.Join("\n  ", names));
    }

    private async Task<ToolResult> SetSceneAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Failed("Paramètre 'name' requis (utilise action=scenes pour lister).");
        await _client.CallAsync("SetCurrentProgramScene", new { sceneName = name }, ct);
        return ToolResult.Succeeded($"Scène « {name} » active. ACTION TERMINÉE.");
    }

    private async Task<ToolResult> SimpleAsync(string requestType, string successMessage, CancellationToken ct = default)
    {
        await _client.CallAsync(requestType, null, ct);
        return ToolResult.Succeeded($"{successMessage} ACTION TERMINÉE.");
    }
}

