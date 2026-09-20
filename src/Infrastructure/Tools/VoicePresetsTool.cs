using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Voice;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Voice preset management tool.
/// Actions: list, select, info, create, delete.
/// Includes 5 JARVIS presets (Iron Man style).
/// </summary>
public sealed class VoicePresetsTool : ITool
{
    private readonly VoicePresetStore _store;
    private readonly ILogger<VoicePresetsTool> _logger;

    public VoicePresetsTool(VoicePresetStore store, ILogger<VoicePresetsTool> logger)
    {
        _store = store;
        _logger = logger;
    }

    public string Name => "presets_voix";
    public string Description =>
        "Gere les presets de voix : lister, selectionner, voir les details, creer un preset personnalise, supprimer. " +
        "Inclut 5 presets JARVIS (Iron Man) : jarvis, jarvis-en, jarvis-deep, jarvis-calm, jarvis-energy.";
    public string Category => "voice";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "list | select | info | create | delete", typeof(string), required: true),
        new("nom", "Nom du preset (select/info/delete)", typeof(string), required: false),
        new("display_name", "Nom d'affichage (create)", typeof(string), required: false),
        new("engine", "Moteur : edge | piper | xtts | windows (create)", typeof(string), required: false),
        new("voice_id", "ID voix moteur (create)", typeof(string), required: false),
        new("vitesse", "Vitesse 0.5-2.0 (create)", typeof(string), required: false)
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("nom", out var nom);
        parameters.TryGetValue("display_name", out var displayName);
        parameters.TryGetValue("engine", out var engine);
        parameters.TryGetValue("voice_id", out var voiceId);
        parameters.TryGetValue("vitesse", out var speedStr);

        try
        {
            return (action?.ToLowerInvariant().Trim()) switch
            {
                "list" => Task.FromResult(List()),
                "select" => Task.FromResult(Select(nom)),
                "info" => Task.FromResult(Info(nom)),
                "create" => Task.FromResult(Create(nom, displayName, engine, voiceId, speedStr)),
                "delete" => Task.FromResult(Delete(nom)),
                _ => Task.FromResult(ToolResult.Failed("Action inconnue : list, select, info, create, delete."))
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VoicePresets] Error");
            return Task.FromResult(ToolResult.Failed($"Erreur : {ex.Message}"));
        }
    }

    private ToolResult List()
    {
        var presets = _store.LoadAll();
        if (presets.Count == 0)
            return ToolResult.Succeeded("Aucun preset disponible.");

        var jarvis = presets.Where(p => p.Name.StartsWith("jarvis")).ToList();
        var others = presets.Where(p => !p.Name.StartsWith("jarvis")).ToList();

        var lines = new List<string>();
        lines.Add("═══ PRESETS JARVIS (Iron Man) ═══");
        foreach (var p in jarvis)
            lines.Add($"  {p.Name,-20} {p.DisplayName,-30} [{p.Engine}] {p.Description}");

        if (others.Count > 0)
        {
            lines.Add("");
            lines.Add("═══ AUTRES PRESETS ═══");
            foreach (var p in others)
                lines.Add($"  {p.Name,-20} {p.DisplayName,-30} [{p.Engine}]");
        }

        lines.Add("");
        lines.Add($"Total: {presets.Count} presets. Selectionne avec : presets_voix select <nom>");

        return ToolResult.Succeeded(string.Join("\n", lines));
    }

    private ToolResult Select(string? nom)
    {
        if (string.IsNullOrWhiteSpace(nom))
            return ToolResult.Failed("Precise le nom du preset a selectionner.");

        var preset = _store.GetByName(nom);
        if (preset is null)
            return ToolResult.Failed($"Preset '{nom}' introuvable. Utilise 'list' pour voir les options.");

        // Update voice settings to use this preset
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "voice-settings.json");

        try
        {
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<Application.Voice.VoiceSettings>(json)
                    ?? new Application.Voice.VoiceSettings();

                settings.TtsVoice = preset.VoiceId;
                settings.TtsEngine = preset.Engine;
                settings.TtsSpeed = preset.Speed;
                settings.Volume = preset.Volume;

                var updated = System.Text.Json.JsonSerializer.Serialize(settings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsPath, updated);
            }
        }
        catch { }

        return ToolResult.Succeeded(
            $"Preset '{preset.DisplayName}' selectionne !\n" +
            $"  Moteur: {preset.Engine}\n" +
            $"  Voix: {preset.VoiceId}\n" +
            $"  Vitesse: {preset.Speed}x\n" +
            $"  Description: {preset.Description}\n" +
            $"Effet apres redemarrage.");
    }

    private ToolResult Info(string? nom)
    {
        if (string.IsNullOrWhiteSpace(nom))
            return ToolResult.Failed("Precise le nom du preset.");

        var preset = _store.GetByName(nom);
        if (preset is null)
            return ToolResult.Failed($"Preset '{nom}' introuvable.");

        return ToolResult.Succeeded(
            $"Preset: {preset.DisplayName}\n" +
            $"ID: {preset.Id}\n" +
            $"Nom: {preset.Name}\n" +
            $"Moteur: {preset.Engine}\n" +
            $"Voice ID: {preset.VoiceId}\n" +
            $"Langue: {preset.Language}\n" +
            $"Vitesse: {preset.Speed}x\n" +
            $"Volume: {preset.Volume}\n" +
            $"System: {(preset.IsSystem ? "Oui (non supprimable)" : "Non")}\n" +
            $"Description: {preset.Description}");
    }

    private ToolResult Create(string? nom, string? displayName, string? engine, string? voiceId, string? speedStr)
    {
        if (string.IsNullOrWhiteSpace(nom))
            return ToolResult.Failed("Precise le nom du nouveau preset.");

        if (_store.GetByName(nom) is not null)
            return ToolResult.Failed($"Le preset '{nom}' existe deja.");

        var speed = float.TryParse(speedStr, out var s) ? s : 1.0f;

        var preset = new JarvisVoicePreset
        {
            Name = nom,
            DisplayName = displayName ?? nom,
            Engine = engine ?? "edge",
            VoiceId = voiceId ?? "",
            Speed = speed,
            IsSystem = false
        };

        _store.Save(preset);
        return ToolResult.Succeeded($"Preset '{nom}' cree ! Selectionne-le avec : presets_voix select {nom}");
    }

    private ToolResult Delete(string? nom)
    {
        if (string.IsNullOrWhiteSpace(nom))
            return ToolResult.Failed("Precise le nom du preset a supprimer.");

        var ok = _store.Delete(nom);
        return ok
            ? ToolResult.Succeeded($"Preset '{nom}' supprime.")
            : ToolResult.Failed($"Preset '{nom}' introuvable ou c'est un preset systeme (non supprimable).");
    }
}
