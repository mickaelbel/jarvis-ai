using JarvisAI.Application.AI;
using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Vision;

public sealed class OllamaVisionService : IVisionService
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaVisionService> _logger;
    private readonly string[] _candidateModels;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private string? _resolvedModel;
    private bool _available;
    private DateTime _lastProbeUtc = DateTime.MinValue;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    public OllamaVisionService(HttpClient httpClient, ILogger<OllamaVisionService> logger, string? preferredModel = null)
    {
        _http = httpClient;
        _logger = logger;
        _candidateModels = new[] { preferredModel, "llava", "llava:7b", "minicpm-v", "moondream", "bakllava" }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ImageDescription> DescribeImageAsync(byte[] imageBytes, string? prompt = null, CancellationToken cancellationToken = default)
    {
        var model = await ResolveModelOrDefaultAsync(cancellationToken);
        return await DescribeWithModelAsync(imageBytes, model, prompt, cancellationToken);
    }

    public async Task<ImageDescription> DescribeImageWithModelAsync(byte[] imageBytes, string model, string? prompt = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return await DescribeImageAsync(imageBytes, prompt, cancellationToken);
        if (!_candidateModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            _logger.LogDebug("[Vision] Using ad-hoc vision model {Model}", model);
        return await DescribeWithModelAsync(imageBytes, model, prompt, cancellationToken);
    }

    public Task<string> ResolveModelAsync(CancellationToken cancellationToken = default)
        => ResolveModelOrDefaultAsync(cancellationToken);

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => await IsAvailableCoreAsync(cancellationToken);

    private async Task<ImageDescription> DescribeWithModelAsync(byte[] imageBytes, string model, string? prompt, CancellationToken cancellationToken)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            return new ImageDescription(string.Empty, false, "Aucune donnée d'image fournie");

        if (string.IsNullOrEmpty(model))
            return new ImageDescription(string.Empty, false, $"Aucun modèle de vision disponible. Testés : {string.Join(", ", _candidateModels)}");

        try
        {
            var userPrompt = string.IsNullOrWhiteSpace(prompt)
                ? "Describe this image in detail. Mention what is visible on screen, including windows, text, buttons and layout."
                : prompt;

            var payload = new
            {
                model,
                stream = false,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = userPrompt,
                        images = new[] { Convert.ToBase64String(imageBytes) }
                    }
                }
            };

            using var response = await _http.PostAsJsonAsync("/api/chat", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;

            if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
                return new ImageDescription(string.Empty, false, "Format de réponse vision inattendu");

            var description = content.GetString() ?? string.Empty;
            _logger.LogInformation("[Vision] Image décrite avec le modèle {Model} ({Length} caractères)", model, description.Length);
            return new ImageDescription(description, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Vision] Image description failed");
            return new ImageDescription(string.Empty, false, $"Erreur vision : {ex.Message}");
        }
    }

    public async Task<MultiImageAnalysis> DescribeMultipleImagesAsync(IReadOnlyList<byte[]> images, string? summaryPrompt = null, CancellationToken cancellationToken = default)
    {
        if (images is null || images.Count == 0)
            return new MultiImageAnalysis(string.Empty, Array.Empty<string>(), false, "Aucune image fournie");

        var model = await ResolveModelOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(model))
            return new MultiImageAnalysis(string.Empty, Array.Empty<string>(), false, $"Aucun modèle de vision disponible. Testés : {string.Join(", ", _candidateModels)}");

        var perImage = new List<string>();
        for (var i = 0; i < images.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return new MultiImageAnalysis(string.Empty, perImage, false, "Annulé");

            var p = i == 0 && !string.IsNullOrWhiteSpace(summaryPrompt)
                ? summaryPrompt
                : $"Décris cette image (image {i + 1}/{images.Count}) en détail.";
            var desc = await DescribeWithModelAsync(images[i], model, p, cancellationToken);
            perImage.Add(desc.Success ? desc.Description : $"<erreur: {desc.ErrorMessage}>");
        }

        var summaryPromptText = string.IsNullOrWhiteSpace(summaryPrompt)
            ? "Fais une synthèse globale de ces descriptions d'images en quelques phrases."
            : summaryPrompt;
        var summary = await DescribeWithModelAsync(FormatPlaceholder(images[0]), model, summaryPromptText + "\n\nDescriptions:\n" + string.Join("\n", perImage), cancellationToken);

        return new MultiImageAnalysis(summary.Success ? summary.Description : string.Empty, perImage, summary.Success, summary.ErrorMessage);
    }

    public async Task<VideoAnalysis> AnalyzeVideoAsync(string videoPath, string? prompt = null, int maxFrames = 8, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            return new VideoAnalysis(string.Empty, Array.Empty<string>(), 0, false, "Fichier vidéo introuvable");

        var ffmpeg = FindFfmpeg();
        if (string.IsNullOrEmpty(ffmpeg))
            return new VideoAnalysis(string.Empty, Array.Empty<string>(), 0, false, "ffmpeg indisponible pour l'extraction de frames");

        var frames = await ExtractFramesAsync(ffmpeg, videoPath, maxFrames, cancellationToken);
        if (frames.Count == 0)
            return new VideoAnalysis(string.Empty, Array.Empty<string>(), 0, false, "Aucune frame n'a pu être extraite");

        var model = await ResolveModelOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(model))
            return new VideoAnalysis(string.Empty, Array.Empty<string>(), frames.Count, false, $"Aucun modèle de vision disponible. Testés : {string.Join(", ", _candidateModels)}");

        var descriptions = new List<string>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return new VideoAnalysis(string.Empty, descriptions, frames.Count, false, "Annulé");
            var p = string.IsNullOrWhiteSpace(prompt)
                ? $"Décris cette frame (frame {i + 1}/{frames.Count}) de la vidéo en détail."
                : prompt + $" (frame {i + 1}/{frames.Count})";
            var desc = await DescribeWithModelAsync(frames[i], model, p, cancellationToken);
            descriptions.Add(desc.Success ? desc.Description : $"<erreur: {desc.ErrorMessage}>");
        }

        var summary = await DescribeWithModelAsync(frames[0], model,
            "Résume la séquence vidéo à partir de ces frames en quelques phrases.\n\nFrames:\n" + string.Join("\n", descriptions),
            cancellationToken);

        return new VideoAnalysis(summary.Success ? summary.Description : string.Empty, descriptions, frames.Count, summary.Success, summary.ErrorMessage);
    }

    public async Task<LocalizedElementsResult> LocalizeAsync(string label, byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            return new LocalizedElementsResult(Array.Empty<VisionElement>(), false, "Aucun libellé fourni");

        var model = await ResolveModelOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(model))
            return new LocalizedElementsResult(Array.Empty<VisionElement>(), false, "Aucun modèle de vision disponible");

        try
        {
            var userPrompt =
                $"Localise tous les éléments correspondant à \"{label}\" dans l'image. " +
                "Réponds UNIQUEMENT en JSON au format [{\"label\":\"...\",\"x\":0,\"y\":0,\"width\":0,\"height\":0}], " +
                "avec x,y coordonnées du coin supérieur gauche et width,height dimensions en pixels. " +
                "Si aucun élément ne correspond, réponds []. Ne mets rien d'autre que le JSON.";

            var payload = new
            {
                model,
                stream = false,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = userPrompt,
                        images = new[] { Convert.ToBase64String(imageBytes) }
                    }
                }
            };

            using var response = await _http.PostAsJsonAsync("/api/chat", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                using var err = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return new LocalizedElementsResult(Array.Empty<VisionElement>(), false,
                    $"Échec de localisation : HTTP {(int)response.StatusCode} {err.RootElement.GetRawText()}");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
                return new LocalizedElementsResult(Array.Empty<VisionElement>(), false, "Format de réponse vision inattendu");

            var json = content.GetString() ?? string.Empty;
            var trimmed = json.Trim();
            if (trimmed.StartsWith("```")) trimmed = trimmed.Trim('`', 'j', 's', 'o', 'n', ' ', '\n', '\r');
            var start = trimmed.IndexOf('[');
            var end = trimmed.LastIndexOf(']');
            if (start < 0 || end <= start)
                return new LocalizedElementsResult(Array.Empty<VisionElement>(), false, "Aucun tableau JSON trouvé dans la réponse du modèle");

            var arr = JsonSerializer.Deserialize<List<LocalizedDto>>(trimmed.Substring(start, end - start + 1));
            var elements = (arr ?? new List<LocalizedDto>())
                .Select(d => new VisionElement(d.label ?? label, d.x, d.y, d.width, d.height, 1.0 - Math.Min(0.5, Math.Abs(d.width * d.height) / 1000000.0)))
                .ToList();
            return new LocalizedElementsResult(elements, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Vision] Échec de localisation");
            return new LocalizedElementsResult(Array.Empty<VisionElement>(), false, $"Erreur de localisation : {ex.Message}");
        }
    }

    private sealed class LocalizedDto
    {
        public string? label { get; set; }
        public int x { get; set; }
        public int y { get; set; }
        public int width { get; set; }
        public int height { get; set; }
    }

    private static byte[] FormatPlaceholder(byte[] image)
    {
        // Réutilise la première image comme support pour la synthèse globale (llava gère une seule image à la fois).
        return image;
    }

    private static async Task<List<byte[]>> ExtractFramesAsync(string ffmpeg, string videoPath, int maxFrames, CancellationToken ct)
    {
        var frames = new List<byte[]>();
        var tempDir = Path.Combine(Path.GetTempPath(), "jarvis_video_frames_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"-i \"{videoPath}\" -vf \"fps=1/{(60 / Math.Max(1, maxFrames))}\" -frames:v {maxFrames} \"{Path.Combine(tempDir, "f%d.png")}\""
            };

            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
            }

            foreach (var file in Directory.GetFiles(tempDir, "f*.png").OrderBy(f => f))
            {
                frames.Add(await File.ReadAllBytesAsync(file, ct));
                if (frames.Count >= maxFrames) break;
            }
        }
        catch
        {
            frames.Clear();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }

        return frames;
    }

    private static string? FindFfmpeg()
    {
        var candidates = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ffmpeg", "bin", "ffmpeg.exe"),
            "ffmpeg.exe"
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        // fallback : ffmpeg sur le PATH
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                if (p.ExitCode == 0) return "ffmpeg";
            }
        }
        catch { }
        return null;
    }

    private async Task<string> ResolveModelOrDefaultAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_resolvedModel))
            return _resolvedModel;
        if (!await IsAvailableCoreAsync(cancellationToken))
            return string.Empty;
        return _resolvedModel ?? string.Empty;
    }

    private async Task<bool> IsAvailableCoreAsync(CancellationToken cancellationToken)
    {
        if (_available) return true;
        if (DateTime.UtcNow - _lastProbeUtc < Cooldown) return false;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_available) return true;
            if (DateTime.UtcNow - _lastProbeUtc < Cooldown) return false;
            _lastProbeUtc = DateTime.UtcNow;

            try
            {
                var tags = await _http.GetFromJsonAsync<OllamaTagsResponse>("/api/tags", cancellationToken);
                if (tags?.Models is not null)
                {
                    foreach (var candidate in _candidateModels)
                    {
                        if (tags.Models.Any(m => string.Equals(m.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                        {
                            _resolvedModel = candidate;
                            _available = true;
                            _logger.LogInformation("[Vision] Modèle résolu : {Model}", candidate);
                            return true;
                        }
                    }
                }

                _logger.LogWarning("[Vision] Aucun modèle de vision trouvé parmi : {Models}", string.Join(", ", _candidateModels));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Vision] Ollama injoignable");
            }

            return false;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private sealed class OllamaTagsResponse
    {
        public List<OllamaModelEntry>? Models { get; set; }
    }

    private sealed class OllamaModelEntry
    {
        public string Name { get; set; } = string.Empty;
    }
}
