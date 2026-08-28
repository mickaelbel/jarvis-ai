using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
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
    private readonly ILogger<ImageGenTool> _logger;

    public string Name => "image_generator";
    public string Description =>
        "Generate an image from a text description (prompt). Free & unlimited API. Use size/ratio words in the prompt " +
        "(ex: 'wide', 'portrait', '4:3') for best results. Returns the image as a base64 data URL.";
    public string Category => "multimedia";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "Je génère ton image…";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("prompt", "Description détaillée de l'image à générer (en anglais c'est mieux). Ex: 'a husky astronaut on the moon, realistic'", typeof(string), required: true),
    };

    public ImageGenTool(IImageGenerationService imageGen, ILogger<ImageGenTool> logger)
    {
        _imageGen = imageGen;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("prompt", out var prompt);
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Failed("Paramètre 'prompt' requis (description de l'image à générer).");

        var result = await _imageGen.GenerateImageAsync(prompt.Trim(), cancellationToken);
        if (result.Success && !string.IsNullOrEmpty(result.DataUrl))
        {
            _logger.LogInformation("[ImageGenTool] Image générée pour : {Prompt}", prompt.Trim());
            return ToolResult.Succeeded(
                $"Image générée (base64 data URL, {result.DataUrl.Length} caractères) :\n{result.DataUrl}");
        }

        return ToolResult.Failed(result.ErrorMessage ?? "Impossible de générer l'image.");
    }
}
