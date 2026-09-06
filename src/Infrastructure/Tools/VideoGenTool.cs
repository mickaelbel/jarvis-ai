using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Génération de vidéos locales via Qwen-Image + ffmpeg.
/// L'outil fonctionne directement, pas besoin de demander des détails supplémentaires.
/// </summary>
public sealed class VideoGenTool : ITool
{
    private readonly IVideoGenerationService _videoGen;
    private readonly IEventBus _eventBus;

    public string Name => "video_generator";
    public string Description =>
        "GÉNÈRE une vidéo en local via CogVideoX-2B (vrai modèle text-to-video, 6 secondes, 720x480). " +
        "Utilise le prompt tel quel, ne demande PAS de détails supplémentaires. " +
        "Le premier lancement prend 3-5 min pour télécharger le modèle (~5GB).";
    public string Category => "multimedia";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "Je génère ta vidéo via CogVideoX (6 secondes, 720x480)… ça peut prendre ~4-5 minutes…";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("prompt", "Description de la scène vidéo (utilise tel quel, ne demande pas plus de détails)", typeof(string), required: true),
        new ToolParameter("duration", "Durée en secondes (défaut 4)", typeof(int), required: false),
        new ToolParameter("ratio", "16:9, 9:16, ou 1:1 (défaut 16:9)", typeof(string), required: false),
    };

    public VideoGenTool(IVideoGenerationService videoGen, IEventBus eventBus)
    {
        _videoGen = videoGen;
        _eventBus = eventBus;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("prompt", out var prompt);
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Failed("Paramètre 'prompt' requis.");

        parameters.TryGetValue("duration", out var durStr);
        var duration = 4;
        if (!string.IsNullOrEmpty(durStr) && int.TryParse(durStr, out var d))
            duration = Math.Clamp(d, 2, 10);

        parameters.TryGetValue("ratio", out var ratio);
        if (string.IsNullOrWhiteSpace(ratio))
            ratio = "16:9";

        var result = await _videoGen.GenerateVideoAsync(prompt.Trim(), duration, ratio, cancellationToken);

        if (!result.Success)
            return ToolResult.Failed(result.ErrorMessage ?? "Échec de la génération vidéo.");

        try
        {
            await _eventBus.PublishAsync(
                new ImageGeneratedEvent(prompt.Trim(), result.Url ?? "", result.FilePath ?? "", context.CorrelationId, "video"),
                cancellationToken);
        }
        catch { }

        return !string.IsNullOrEmpty(result.FilePath)
            ? ToolResult.Succeeded($"Vidéo générée : {result.FilePath}")
            : ToolResult.Succeeded("Vidéo générée.");
    }
}
