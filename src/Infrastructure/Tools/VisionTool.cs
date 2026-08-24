using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class VisionTool : ITool
{
    private readonly IComputerController _controller;
    private readonly IOcrService _ocr;
    private readonly IVisionService _vision;
    private readonly ILogger<VisionTool> _logger;

    public string Name => "vision";
    public string Description => "Vois et lis l'écran comme un humain. Utilise-le quand l'utilisateur dit « c'est quoi cette erreur ? », « lis ça », « qu'est-ce que tu vois ? », « traduis l'écran ». Actions : screen_describe (décris/analyse ce qui est à l'écran, paramètre prompt = la question de l'utilisateur), screen_ocr (extrait le texte exact de l'écran), image_describe (décris un fichier image, paramètre path + prompt), image_ocr (texte d'un fichier image).";
    public string Category => "vision";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "Je regarde ton écran…";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Operation: screen_ocr, screen_describe, image_ocr, image_describe", typeof(string), required: true),
        new ToolParameter("path", "Path of the image file (for image_ocr / image_describe)", typeof(string)),
        new ToolParameter("prompt", "Optional instruction for describe actions", typeof(string))
    };

    public VisionTool(IComputerController controller, IOcrService ocr, IVisionService vision, ILogger<VisionTool> logger)
    {
        _controller = controller;
        _ocr = ocr;
        _vision = vision;
        _logger = logger;
        // Disponibilité du modèle vision vérifiée en tâche de fond (jamais
        // dans le chemin critique) puis rafraîchie toutes les 5 minutes.
        _ = RefreshAvailabilityLoopAsync();
    }

    private volatile bool _visionModelAvailable;
    private static readonly HttpClient TagsHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Vision inutilisable sans modèle local (llava…) : l'outil est
    /// retiré de la liste du modèle pour éviter des appels en boucle qui échouent.</summary>
    public bool IsAvailable => _visionModelAvailable;

    private async Task RefreshAvailabilityLoopAsync()
    {
        while (true)
        {
            try
            {
                var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_URL") is { Length: > 0 } u
                    ? u.TrimEnd('/') : "http://127.0.0.1:11434";
                using var reponse = await TagsHttp.GetAsync($"{baseUrl}/api/tags");
                if (reponse.IsSuccessStatusCode)
                {
                    var json = await reponse.Content.ReadAsStringAsync();
                    _visionModelAvailable = json.Contains("llava") || json.Contains("minicpm")
                        || json.Contains("vl") || json.Contains("moondream") || json.Contains("bakllava");
                }
                else
                {
                    _visionModelAvailable = false;
                }
            }
            catch
            {
                _visionModelAvailable = false;
            }
            await Task.Delay(TimeSpan.FromMinutes(5));
        }
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("path", out var path);
        parameters.TryGetValue("prompt", out var prompt);

        try
        {
            return action?.ToLowerInvariant() switch
            {
                "screen_ocr" => await ScreenOcrAsync(cancellationToken),
                "screen_describe" => await ScreenDescribeAsync(prompt, cancellationToken),
                "image_ocr" => await ImageOcrAsync(path, cancellationToken),
                "image_describe" => await ImageDescribeAsync(path, prompt, cancellationToken),
                _ => ToolResult.Failed($"Unknown action: {action}. Valid: screen_ocr, screen_describe, image_ocr, image_describe")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VisionTool] Action {Action} failed", action);
            return ToolResult.Failed($"Vision error: {ex.Message}");
        }
    }

    private async Task<ToolResult> ScreenOcrAsync(CancellationToken cancellationToken)
    {
        if (!_controller.IsAvailable)
            return ToolResult.Failed("Screen capture is only available on Windows");

        var capture = await _controller.CaptureScreenAsync(cancellationToken);
        if (capture is null)
            return ToolResult.Failed("Failed to capture the screen");

        var ocr = await _ocr.ExtractTextAsync(capture.PngBytes, cancellationToken: cancellationToken);
        if (ocr is null)
            return ToolResult.Failed("OCR is unavailable (tessdata missing or non-Windows)");

        var payload = new
        {
            text = ocr.Text,
            words = ocr.Words.Select(w => new { w.Text, w.X, w.Y, w.Width, w.Height })
        };

        _logger.LogInformation("[VisionTool] Screen OCR: {Words} words, {Chars} chars", ocr.Words.Count, ocr.Text.Length);
        return ToolResult.Succeeded(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<ToolResult> ScreenDescribeAsync(string? prompt, CancellationToken cancellationToken)
    {
        if (!_controller.IsAvailable)
            return ToolResult.Failed("Screen capture is only available on Windows");

        var capture = await _controller.CaptureScreenAsync(cancellationToken);
        if (capture is null)
            return ToolResult.Failed("Failed to capture the screen");

        var description = await _vision.DescribeImageAsync(capture.PngBytes, prompt, cancellationToken);
        return description.Success
            ? ToolResult.Succeeded(description.Description)
            : ToolResult.Failed(description.ErrorMessage ?? "Vision model unavailable");
    }

    private async Task<ToolResult> ImageOcrAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return ToolResult.Failed("Parameter 'path' is required and must point to an existing image file");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var ocr = await _ocr.ExtractTextAsync(bytes, cancellationToken: cancellationToken);
        if (ocr is null)
            return ToolResult.Failed("OCR is unavailable (tessdata missing or non-Windows)");

        var payload = new
        {
            text = ocr.Text,
            words = ocr.Words.Select(w => new { w.Text, w.X, w.Y, w.Width, w.Height })
        };
        return ToolResult.Succeeded(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<ToolResult> ImageDescribeAsync(string? path, string? prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return ToolResult.Failed("Parameter 'path' is required and must point to an existing image file");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var description = await _vision.DescribeImageAsync(bytes, prompt, cancellationToken);
        return description.Success
            ? ToolResult.Succeeded(description.Description)
            : ToolResult.Failed(description.ErrorMessage ?? "Vision model unavailable");
    }
}
