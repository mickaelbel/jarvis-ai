using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.AI;

public interface IMultiModalProcessor
{
    Task<MultiModalResult> ProcessAsync(byte[] imageData, string? textPrompt = null, CancellationToken ct = default);
    bool IsImageSupported(string fileName);
    string GetSupportedFormats();
}

public sealed class MultiModalProcessor : IMultiModalProcessor
{
    private readonly ILogger<MultiModalProcessor> _logger;

    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif"
    };

    public MultiModalProcessor(ILogger<MultiModalProcessor> logger)
    {
        _logger = logger;
    }

    public async Task<MultiModalResult> ProcessAsync(byte[] imageData, string? textPrompt = null, CancellationToken ct = default)
    {
        try
        {
            var base64 = Convert.ToBase64String(imageData);
            var imageInfo = AnalyzeImage(imageData);

            var prompt = !string.IsNullOrWhiteSpace(textPrompt)
                ? textPrompt
                : "Décris cette image en détail.";

            _logger.LogInformation("[MultiModal] Processing image: {Format}, {Size}KB, prompt: {Prompt}",
                imageInfo.Format, imageData.Length / 1024, prompt[..Math.Min(50, prompt.Length)]);

            var result = new MultiModalResult
            {
                Success = true,
                ImageInfo = imageInfo,
                Prompt = prompt,
                Base64Image = base64,
                ProcessedAt = DateTime.UtcNow
            };

            return await Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MultiModal] Processing failed");
            return new MultiModalResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public bool IsImageSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return SupportedFormats.Contains(ext);
    }

    public string GetSupportedFormats()
    {
        return string.Join(", ", SupportedFormats);
    }

    private ImageInfo AnalyzeImage(byte[] data)
    {
        var info = new ImageInfo
        {
            SizeBytes = data.Length,
            Format = "unknown"
        };

        if (data.Length >= 4)
        {
            if (data[0] == 0xFF && data[1] == 0xD8)
                info.Format = "JPEG";
            else if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                info.Format = "PNG";
            else if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
                info.Format = "GIF";
            else if (data[0] == 0x42 && data[1] == 0x4D)
                info.Format = "BMP";
        }

        return info;
    }
}

public sealed class MultiModalResult
{
    public bool Success { get; set; }
    public ImageInfo? ImageInfo { get; set; }
    public string Prompt { get; set; } = "";
    public string Base64Image { get; set; } = "";
    public string? Analysis { get; set; }
    public string? Error { get; set; }
    public DateTime ProcessedAt { get; set; }
}

public sealed class ImageInfo
{
    public string Format { get; set; } = "";
    public long SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double SizeMb => SizeBytes / 1024.0 / 1024.0;
}
