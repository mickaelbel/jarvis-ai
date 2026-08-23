namespace JarvisAI.Application.Memory;

public interface IEmbeddingService
{
    string? ModelName { get; }
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<float[]?> GenerateAsync(string text, CancellationToken cancellationToken = default);
}
