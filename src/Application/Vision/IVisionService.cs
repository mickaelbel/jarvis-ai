namespace JarvisAI.Application.Vision;

public sealed record ImageDescription(string Description, bool Success, string? ErrorMessage);

public interface IVisionService
{
    Task<ImageDescription> DescribeImageAsync(byte[] imageBytes, string? prompt = null, CancellationToken cancellationToken = default);
}
