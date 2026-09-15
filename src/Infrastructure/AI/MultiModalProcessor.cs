using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AI;

public interface IMultiModalProcessor
{
    Task<MultiModalResult> ProcessAsync(byte[] imageData, string? textPrompt = null, CancellationToken ct = default);
    bool IsImageSupported(string fileName);
    string GetSupportedFormats();
}

/// <summary>
/// Analyse visuelle d'une image par un modèle de vision local (Ollama, ex llava /
/// qwen2.5vl / minicpm-v) via /api/generate. Contrairement à VisionClient
/// (capture d'écran), ce processeur consomme des octets fournis par l'appelant.
/// </summary>
public sealed class MultiModalProcessor : IMultiModalProcessor
{
    private const string DefaultVisionModel = "llava";
    private const int MaxImageBytes = 20 * 1024 * 1024;

    private readonly ILogger<MultiModalProcessor> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _visionModel;

    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif"
    };

    public MultiModalProcessor(HttpClient httpClient, ILogger<MultiModalProcessor> logger, string? visionModel = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _visionModel = string.IsNullOrWhiteSpace(visionModel) ? DefaultVisionModel : visionModel.Trim();
    }

    public async Task<MultiModalResult> ProcessAsync(byte[] imageData, string? textPrompt = null, CancellationToken ct = default)
    {
        if (imageData is null || imageData.Length == 0)
            return Fail("Image vide : aucun octet à analyser.");

        if (imageData.Length > MaxImageBytes)
            return Fail($"Image trop volumineuse ({imageData.Length / 1024 / 1024} Mo > {MaxImageBytes / 1024 / 1024} Mo).");

        try
        {
            var imageInfo = AnalyzeImage(imageData);
            var prompt = !string.IsNullOrWhiteSpace(textPrompt)
                ? textPrompt!.Trim()
                : "Décris cette image en détail.";
            var base64 = Convert.ToBase64String(imageData);

            _logger.LogInformation("[MultiModal] Analyse {Format}, {Size}KB avec {Model}, prompt « {Prompt} »",
                imageInfo.Format, imageData.Length / 1024, _visionModel, prompt[..Math.Min(60, prompt.Length)]);

            var payload = JsonSerializer.Serialize(new
            {
                model = _visionModel,
                prompt,
                images = new[] { base64 },
                stream = false
            });

            using var contenu = new StringContent(payload, Encoding.UTF8, "application/json");
            using var reponse = await _httpClient.PostAsync("/api/generate", contenu, ct);

            if (!reponse.IsSuccessStatusCode)
                return Fail($"Le modèle de vision « {_visionModel} » a répondu HTTP {(int)reponse.StatusCode}. " +
                            "Vérifie que Ollama est lancé et que le modèle est installé.");

            var json = await reponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var analyse = doc.RootElement.TryGetProperty("response", out var r)
                ? r.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(analyse))
                return Fail("Le modèle de vision n'a rien renvoyé pour cette image.");

            return new MultiModalResult
            {
                Success = true,
                ImageInfo = imageInfo,
                Prompt = prompt,
                Base64Image = base64,
                Analysis = analyse.Trim(),
                Model = _visionModel,
                LatencyMs = 0,
                ProcessedAt = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[MultiModal] Annulé par l'appelant");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MultiModal] Échec de l'analyse ({Model})", _visionModel);
            return Fail($"Analyse impossible : {ex.Message}");
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

    private static MultiModalResult Fail(string message)
        => new() { Success = false, Error = message, ProcessedAt = DateTime.UtcNow };

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
    public string? Model { get; set; }
    public long LatencyMs { get; set; }
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