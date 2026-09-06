using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Génération d'images locales via Qwen-Image-2.0 (diffusers, CUDA).
/// Gère automatiquement le serveur Python HTTP (scripts/qwen_image_server.py).
/// 100% local, gratuit, illimité. RTX 4060 Ti 16GB = ~14GB VRAM en FP16.
/// </summary>
public sealed class QwenImageService : IImageGenerationService, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<QwenImageService> _logger;
    private Process? _serverProcess;
    private readonly object _lock = new();
    private volatile bool _serverReady;
    private volatile bool _serverFailed;

    private const int Port = 8189;
    private const string ApiBase = "http://127.0.0.1:8189";
    private static readonly string ServerScript = FindServerScript();
    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "JarvisAI");

    public QwenImageService(HttpClient httpClient, ILogger<QwenImageService> logger)
    {
        _http = httpClient;
        _http.Timeout = TimeSpan.FromMinutes(4);
        _logger = logger;
    }

    public async Task<GeneratedImage> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return new GeneratedImage(null, null, false, "Un prompt est requis.");

        if (!await EnsureServerRunningAsync(cancellationToken))
            return new GeneratedImage(null, null, false,
                "Impossible de démarrer le serveur Qwen-Image. Vérifie que Python et PyTorch sont installés.");

        try
        {
            var payload = new { prompt, width = 1024, height = 1024, steps = 28 };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync($"{ApiBase}/generate", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new GeneratedImage(null, null, false, $"Serveur Qwen-Image: {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                var error = doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : "Erreur inconnue";
                return new GeneratedImage(null, null, false, error);
            }

            var b64 = doc.RootElement.GetProperty("image").GetString()!;
            var dataUrl = $"data:image/png;base64,{b64}";

            // Sauvegarder sur disque
            Directory.CreateDirectory(OutputDir);
            var filePath = Path.Combine(OutputDir, $"jarvis-image-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(b64), cancellationToken);

            _logger.LogInformation("[QwenImage] Image générée : {Path}", filePath);
            return new GeneratedImage(dataUrl, filePath, true, null);
        }
        catch (OperationCanceledException)
        {
            return new GeneratedImage(null, null, false, "Génération annulée.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[QwenImage] Échec de génération");
            return new GeneratedImage(null, null, false, $"Erreur Qwen-Image : {ex.Message}");
        }
    }

    private async Task<bool> EnsureServerRunningAsync(CancellationToken ct)
    {
        // Vérifier si le serveur tourne déjà
        if (await IsServerReadyAsync(ct)) return true;

        if (_serverFailed) return false;

        lock (_lock)
        {
            if (_serverProcess is { HasExited: false }) return true;

            if (!File.Exists(ServerScript))
            {
                _logger.LogWarning("[QwenImage] Script introuvable : {Script}", ServerScript);
                _serverFailed = true;
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{ServerScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                _serverProcess = Process.Start(psi);
                if (_serverProcess is null) { _serverFailed = true; return false; }
                _logger.LogInformation("[QwenImage] Serveur démarré (PID {PID})", _serverProcess.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[QwenImage] Échec de démarrage du serveur");
                _serverFailed = true;
                return false;
            }
        }

        // Attendre que le serveur soit prêt (max 120s pour charger le modèle)
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsServerReadyAsync(ct))
            {
                _serverReady = true;
                return true;
            }
            await Task.Delay(3000, ct);
        }

        _logger.LogWarning("[QwenImage] Délai dépassé pour le serveur");
        return false;
    }

    private async Task<bool> IsServerReadyAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ApiBase}/health", ct);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("status", out var s) &&
                   s.GetString() == "ok";
        }
        catch
        {
            return false;
        }
    }

    private static string FindServerScript()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "scripts", "qwen_image_server.py"),
            Path.Combine(baseDir, "..", "..", "..", "scripts", "qwen_image_server.py"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", "scripts", "qwen_image_server.py"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return Path.GetFullPath(candidates[0]);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_serverProcess is { HasExited: false })
            {
                try { _serverProcess.Kill(entireProcessTree: true); } catch { }
            }
            _serverProcess?.Dispose();
            _serverProcess = null;
        }
        await ValueTask.CompletedTask;
    }
}
