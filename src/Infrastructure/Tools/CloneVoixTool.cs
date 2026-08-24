using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Voice;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// « Clone ta voix sur la mienne » : envoie un WAV de référence (6-30 s) au
/// serveur XTTS local, puis bascule TtsEngine sur xtts.
/// </summary>
public sealed class CloneVoixTool : ITool
{
    private readonly XttsTextToSpeechService _xtts;
    private readonly Application.Voice.IVoiceSettingsStore _settingsStore;

    public CloneVoixTool(XttsTextToSpeechService xtts, Application.Voice.IVoiceSettingsStore settingsStore)
    {
        _xtts = xtts;
        _settingsStore = settingsStore;
    }

    public string Name => "clone_ma_voix";
    public string Description =>
        "Clone une voix à partir d'un fichier WAV de référence (6 à 30 secondes de parole propre) et active le moteur XTTS. " +
        "Après clonage, Jarvis parle avec cette voix.";
    public string Category => "voice";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("chemin_wav", "Chemin complet du WAV de référence (6-30 s, voix seule)", typeof(string), required: true)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            parameters ??= new Dictionary<string, string>();
            var chemin = parameters.TryGetValue("chemin_wav", out var c) ? c.Trim().Trim('"') : "";
            if (chemin.Length == 0)
                return ToolResult.Failed("Précise le chemin du WAV de référence (paramètre chemin_wav).");

            var ok = await _xtts.ClonerVoixAsync(chemin, cancellationToken);
            if (!ok)
                return ToolResult.Failed("Le serveur XTTS n'est pas joignable. Lance-le d'abord : python scripts/tts_xtts_server.py");

            var settings = _settingsStore.Get();
            if (!settings.TtsEngine.Equals("xtts", StringComparison.OrdinalIgnoreCase))
            {
                settings.TtsEngine = "xtts";
                _settingsStore.Save(settings);
                return ToolResult.Succeeded($"Voix clonée depuis {Path.GetFileName(chemin)} et moteur XTTS activé. Elle sera utilisée après redémarrage de Jarvis.");
            }
            return ToolResult.Succeeded($"Voix clonée depuis {Path.GetFileName(chemin)}. Nouvelle voix active après redémarrage.");
        }
        catch (FileNotFoundException ex)
        {
            return ToolResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Clonage impossible : {ex.Message}");
        }
    }
}
