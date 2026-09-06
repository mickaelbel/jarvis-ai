using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Génération d'images locale via Qwen-Image (diffusers, CUDA).
/// Le serveur Python démarre automatiquement au premier appel.
/// </summary>
public sealed class ImageGenTool : ITool
{
    private readonly IImageGenerationService _imageGen;
    private readonly IEventBus _eventBus;
    private readonly ILogger<ImageGenTool> _logger;

    public string Name => "image_generator";
    public string Description =>
        "GÉNÈRE une image via Qwen-Image (modèle local, gratuit, illimité). " +
        "UTILISE UNIQUEMENT quand l'utilisateur DEMANDE EXPLICITEMENT de générer/créer une image. " +
        "Le premier lancement peut prendre 1-2 minutes pour charger le modèle (~14 Go VRAM).";
    public string Category => "multimedia";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "Je génère ton image… ça peut prendre ~20 secondes…";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("prompt", "Description détaillée de l'image à générer (en anglais pour meilleur résultat)", typeof(string), required: true),
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
            return ToolResult.Failed("Paramètre 'prompt' requis. Décris l'image que tu veux générer.");

        var cleaned = SanitizePrompt(prompt.Trim());
        _logger.LogInformation("[ImageGen] Génération demandée : {Prompt}", prompt.Trim());

        var result = await _imageGen.GenerateImageAsync(cleaned, cancellationToken);

        if (!result.Success)
            return ToolResult.Failed(result.ErrorMessage ?? "Échec de la génération d'image.");

        // Le service sauvegarde déjà sur disque et retourne le chemin dans Url
        var filePath = result.Url ?? "";

        try
        {
            await _eventBus.PublishAsync(
                new ImageGeneratedEvent(prompt.Trim(), result.DataUrl ?? "", filePath, context.CorrelationId),
                cancellationToken);
        }
        catch { }

        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            return ToolResult.Succeeded($"Image générée et enregistrée : {filePath}");

        return ToolResult.Succeeded("Image générée avec succès.");
    }

    private static string SanitizePrompt(string prompt)
    {
        var result = prompt;

        // Ajouter des boosters de qualité si absents
        var qualityBoosters = new[] { "photorealistic", "high quality", "detailed" };
        if (!qualityBoosters.Any(q => result.Contains(q, StringComparison.OrdinalIgnoreCase)))
            result += ", high quality, detailed";

        return result;
    }
}
