namespace JarvisAI.Application.Vision;

public sealed record OcrWord(string Text, int X, int Y, int Width, int Height);

public sealed record OcrResult(string Text, IReadOnlyList<OcrWord> Words);

public interface IOcrService
{
    bool OcrAvailable { get; }
    Task<OcrResult?> ExtractTextAsync(byte[] imageBytes, string language = "fra+eng", CancellationToken cancellationToken = default);
}
