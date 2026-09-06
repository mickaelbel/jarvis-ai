namespace JarvisAI.Application.Vision;

public sealed record GeneratedVideo(string? FilePath, string? Url, bool Success, string? ErrorMessage);

/// <summary>
/// Génération de vidéos à partir d'un prompt texte. MiniMax H3 via API cloud.
/// </summary>
public interface IVideoGenerationService
{
    Task<GeneratedVideo> GenerateVideoAsync(string prompt, int durationSeconds = 5, string ratio = "16:9", CancellationToken cancellationToken = default);
}
