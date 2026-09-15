using System.Collections.Generic;

namespace JarvisAI.Application.Vision;

public sealed record ImageDescription(string Description, bool Success, string? ErrorMessage);

public sealed record VisionElement(string Label, int X, int Y, int Width, int Height, double Confidence);

public sealed record MultiImageAnalysis(string Summary, IReadOnlyList<string> PerImage, bool Success, string? ErrorMessage);

public sealed record VideoAnalysis(string Summary, IReadOnlyList<string> FrameDescriptions, int FrameCount, bool Success, string? ErrorMessage);

public sealed record LocalizedElementsResult(IReadOnlyList<VisionElement> Elements, bool Success, string? ErrorMessage);

/// <summary>
/// Service de vision locale intégré. Décrit une image, plusieurs images, un document,
/// analyse une vidéo par extraction de frames et localise des éléments visuels avec coordonnées.
/// Le choix du modèle de vision est délégué au routeur.
/// </summary>
public interface IVisionService
{
    Task<ImageDescription> DescribeImageAsync(byte[] imageBytes, string? prompt = null, CancellationToken cancellationToken = default);

    Task<ImageDescription> DescribeImageWithModelAsync(byte[] imageBytes, string model, string? prompt = null, CancellationToken cancellationToken = default);

    Task<MultiImageAnalysis> DescribeMultipleImagesAsync(IReadOnlyList<byte[]> images, string? summaryPrompt = null, CancellationToken cancellationToken = default);

    Task<VideoAnalysis> AnalyzeVideoAsync(string videoPath, string? prompt = null, int maxFrames = 8, CancellationToken cancellationToken = default);

    Task<LocalizedElementsResult> LocalizeAsync(string label, byte[] imageBytes, CancellationToken cancellationToken = default);

    Task<string> ResolveModelAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Garantit qu'un modèle de vision est installé : si Ollama est joignable mais
    /// qu'aucun modèle candidat n'est présent, télécharge automatiquement le modèle
    /// préféré puis le résout. Retourne true si un modèle est finalement utilisable.
    /// </summary>
    Task<bool> EnsureVisionModelAsync(CancellationToken cancellationToken = default);
}
