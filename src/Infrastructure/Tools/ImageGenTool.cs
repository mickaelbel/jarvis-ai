using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Génère une image à partir d'un prompt texte. Backend 100% gratuit et illimité
/// (API Pollinations, sans clé). Pour une génération « full local », l'utilisateur
/// peut installer un modèle de diffusion local (voir Settings).
/// </summary>
public sealed class ImageGenTool : ITool
{
    private readonly IImageGenerationService _imageGen;
    private readonly IEventBus _eventBus;
    private readonly ILogger<ImageGenTool> _logger;

    public string Name => "image_generator";
    public string Description =>
        "CRÉER/GÉNÉRER une image numérique à partir d'un texte. Utilise ceci pour « dessine », « génère une image », " +
        "« crée une image de… », « illustre … », ou quand l'utilisateur décrit une scène à représenter (ex: une voiture " +
        "au bord d'un lac dans les montagnes). N'utilise PAS vision pour générer une image : vision sert à ANALYSER une " +
        "image fournie, image_generator sert à en CRÉER. L'image générée est affichée automatiquement dans le chat " +
        "et enregistrée dans le dossier Images. Gratuit & illimité.";
    public string Category => "multimedia";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "Je génère ton image…";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("prompt", "Description détaillée de l'image à générer (en anglais c'est mieux). Ex: 'a husky astronaut on the moon, realistic'", typeof(string), required: true),
    };

    public ImageGenTool(IImageGenerationService imageGen, IEventBus eventBus, ILogger<ImageGenTool> logger)
    {
        _imageGen = imageGen;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("prompt", out var prompt);
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Failed("Paramètre 'prompt' requis (description de l'image à générer).");

        var cleaned = SanitizePrompt(prompt.Trim());
        var result = await _imageGen.GenerateImageAsync(cleaned, cancellationToken);
        if (!result.Success || string.IsNullOrEmpty(result.DataUrl))
            return ToolResult.Failed(result.ErrorMessage ?? "Impossible de générer l'image.");

        try
        {
            var filePath = SaveToDisk(result.DataUrl);
            _logger.LogInformation("[ImageGenTool] Image générée pour : {Prompt} -> {File}", prompt.Trim(), filePath);

            await _eventBus.PublishAsync(
                new ImageGeneratedEvent(prompt.Trim(), result.DataUrl, filePath, context.CorrelationId),
                cancellationToken);

            return ToolResult.Succeeded(
                $"Image générée et affichée dans le chat. Fichier enregistré : {filePath}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ImageGenTool] Échec d'enregistrement de l'image générée");
            return ToolResult.Succeeded("Image générée et affichée dans le chat (enregistrement disque impossible).");
        }
    }

    private static string SanitizePrompt(string prompt)
    {
        // Corrige les erreurs de traduction fr→en du LLM : « autour d'un lac »
        // est traduit « floating around a lake » → voiture flottante dans l'eau.
        // Utilise des patterns plus larges pour catch les variations.
        var fixups = new (string From, string To)[]
        {
            ("floating around a tranquil lake", "parked on the shore of a tranquil lake"),
            ("floating on a tranquil lake", "parked beside a tranquil lake"),
            ("floating around a calm lake", "parked on the shore of a calm lake"),
            ("floating on a calm lake", "parked beside a calm lake"),
            ("floating around a lake", "parked on the shore of a lake"),
            ("floating on a lake", "parked beside a lake"),
            ("floating around", "near"),
            ("floating on", "on the shore of"),
            ("floating in", "in"),
            ("driving around a lake", "driving near a lake"),
            ("driving on a lake", "driving beside a lake"),
            ("on a lake", "beside a lake"),
            ("in a lake", "beside a lake"),
        };
        var result = prompt;
        foreach (var (from, to) in fixups)
            result = result.Replace(from, to, StringComparison.OrdinalIgnoreCase);

        // Boost qualité : ajouter des descripteurs si absents
        var qualityBoosters = new[] { "photorealistic", "8k", "highly detailed", "professional photography", "cinematic lighting" };
        var hasQuality = qualityBoosters.Any(q => result.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (!hasQuality)
            result += ", photorealistic, highly detailed, professional photography, cinematic lighting, 8k resolution";

        return result;
    }

    private static string SaveToDisk(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) throw new InvalidOperationException("data URL invalide");
        var meta = dataUrl[..comma];
        var base64 = dataUrl[(comma + 1)..];
        var bytes = Convert.FromBase64String(base64);

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "JarvisAI");
        Directory.CreateDirectory(dir);
        var ext = meta.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
        var filePath = Path.Combine(dir, $"jarvis-image-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}");
        File.WriteAllBytes(filePath, bytes);
        return filePath;
    }
}
