using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IScreenshotAnnotator
{
    Task<string> AnnotateAsync(byte[] imageData, IReadOnlyList<Annotation> annotations, CancellationToken ct = default);
    IReadOnlyList<string> GetAnnotationTypes();
}

public sealed class ScreenshotAnnotator : IScreenshotAnnotator
{
    private readonly ILogger<ScreenshotAnnotator> _logger;

    public ScreenshotAnnotator(ILogger<ScreenshotAnnotator> logger)
    {
        _logger = logger;
    }

    public async Task<string> AnnotateAsync(byte[] imageData, IReadOnlyList<Annotation> annotations, CancellationToken ct = default)
    {
        try
        {
            var base64 = Convert.ToBase64String(imageData);

            var annotationSummary = annotations.Select(a => a.Type switch
            {
                "arrow" => $"Flèche de ({a.X1},{a.Y1}) vers ({a.X2},{a.Y2})",
                "highlight" => $"Surlignage à ({a.X1},{a.Y1}) [{a.Color}]",
                "text" => $"Texte '{a.Text}' à ({a.X1},{a.Y1})",
                "blur" => $"Floutage à ({a.X1},{a.Y1}) {a.Width}x{a.Height}",
                "rect" => $"Rectangle à ({a.X1},{a.Y1}) {a.Width}x{a.Height}",
                _ => $"Annotation {a.Type}"
            });

            var result = new
            {
                imageBase64 = base64,
                annotations = annotations.Count,
                details = annotationSummary,
                processedAt = DateTime.UtcNow
            };

            _logger.LogInformation("[Annotator] Applied {Count} annotations to image ({Size}KB)",
                annotations.Count, imageData.Length / 1024);

            return await Task.FromResult(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Annotator] Annotation failed");
            return $"Error: {ex.Message}";
        }
    }

    public IReadOnlyList<string> GetAnnotationTypes()
    {
        return new[] { "arrow", "highlight", "text", "blur", "rect", "circle", "freehand" };
    }
}

public sealed class Annotation
{
    public string Type { get; set; } = "";
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Text { get; set; }
    public string Color { get; set; } = "#FF0000";
    public int StrokeWidth { get; set; } = 2;
}
