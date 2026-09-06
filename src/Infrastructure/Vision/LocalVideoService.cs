using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Génération de vidéos locales via CogVideoX-2B (diffusers, CUDA).
/// 100% local, gratuit, illimité. RTX 4060 Ti 16GB.
/// Vidéo de 6 secondes, 720x480, 8 FPS.
/// </summary>
public sealed class LocalVideoService : IVideoGenerationService, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<LocalVideoService> _logger;
    private Process? _serverProcess;
    private readonly object _lock = new();
    private volatile bool _serverFailed;

    private const int Port = 8190;
    private const string ApiBase = "http://127.0.0.1:8190";
    private static readonly string ServerScript = FindServerScript();

    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(10);

    public LocalVideoService(HttpClient httpClient, ILogger<LocalVideoService> logger)
    {
        _http = httpClient;
        _logger = logger;
    }

    public async Task<GeneratedVideo> GenerateVideoAsync(
        string prompt, int durationSeconds = 5, string ratio = "16:9",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return new GeneratedVideo(null, null, false, "Un prompt est requis.");

        if (!await EnsureServerRunningAsync(cancellationToken))
            return new GeneratedVideo(null, null, false,
                "❌ Serveur vidéo non disponible. Vérifie que Python et PyTorch sont installés.");

        try
        {
            // CogVideoX-2B: 720x480 fixe, 49 frames = 6 secondes à 8 FPS
            var payload = new
            {
                prompt,
                width = 720,
                height = 480,
                steps = 30,
                guidance_scale = 6.0
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            _logger.LogInformation("[LocalVideo] Génération : {Prompt} (720x480, 49 frames, 6s)", prompt);

            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(GenerationTimeout);
            using var response = await _http.PostAsync($"{ApiBase}/generate", content, requestCts.Token);
            var body = await response.Content.ReadAsStringAsync(requestCts.Token);

            if (!response.IsSuccessStatusCode)
                return new GeneratedVideo(null, null, false,
                    $"❌ Serveur vidéo indisponible (HTTP {(int)response.StatusCode}). Le modèle est peut-être encore en cours de téléchargement.");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                var error = doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : "Erreur inconnue";
                return new GeneratedVideo(null, null, false, error);
            }

            var videoPath = doc.RootElement.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? "" : "";

            _logger.LogInformation("[LocalVideo] Vidéo générée : {Path}", videoPath);
            return new GeneratedVideo(videoPath, videoPath, true, null);
        }
        catch (OperationCanceledException)
        {
            return new GeneratedVideo(null, null, false, "Génération annulée.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LocalVideo] Échec de génération");
            return new GeneratedVideo(null, null, false, $"Erreur vidéo : {ex.Message}");
        }
    }

    private async Task<bool> EnsureServerRunningAsync(CancellationToken ct)
    {
        if (await IsServerReadyAsync(ct)) return true;
        if (_serverFailed)
        {
            _serverFailed = false;
            return false;
        }

        lock (_lock)
        {
            if (_serverProcess is { HasExited: false }) return true;

            if (!File.Exists(ServerScript))
            {
                _logger.LogWarning("[LocalVideo] Script introuvable : {Script}", ServerScript);
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
                _serverProcess.OutputDataReceived += (s, e) => { if (e.Data is not null) _logger.LogDebug("[LocalVideo-Server] {Line}", e.Data); };
                _serverProcess.ErrorDataReceived += (s, e) => { if (e.Data is not null) _logger.LogWarning("[LocalVideo-Server] {Line}", e.Data); };
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
                _logger.LogInformation("[LocalVideo] Serveur démarré (PID {PID})", _serverProcess.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LocalVideo] Échec de démarrage");
                _serverFailed = true;
                return false;
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsServerReadyAsync(ct)) return true;
            await Task.Delay(5000, ct);
        }

        _logger.LogWarning("[LocalVideo] Délai dépassé pour le serveur");
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
            return doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "ok";
        }
        catch { return false; }
    }

    private static string FindServerScript()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "scripts", "local_video_server.py"),
            Path.Combine(baseDir, "..", "..", "..", "scripts", "local_video_server.py"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", "scripts", "local_video_server.py"),
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
