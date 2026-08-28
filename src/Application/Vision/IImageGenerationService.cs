namespace JarvisAI.Application.Vision;

public sealed record GeneratedImage(string? DataUrl, string? Url, bool Success, string? ErrorMessage);

/// <summary>
/// Génération d'images à partir d'un prompt texte. Les implémentations peuvent
/// être locales (modèle Stable Diffusion via API) ou gratuites illimitées (API
/// Pollinations). On privilégie une source 100% gratuite et sans clé.
/// </summary>
public interface IImageGenerationService
{
    Task<GeneratedImage> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default);
}
