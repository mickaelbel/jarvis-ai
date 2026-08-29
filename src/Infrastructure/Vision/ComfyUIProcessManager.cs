using JarvisAI.Application.Abstractions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Gère le processus ComfyUI en arrière-plan : le démarre automatiquement
/// quand un GPU Nvidia est détecté, vérifie qu'il est prêt (API accessible),
/// et l'arrête proprement à la fermeture de l'application. Le chemin ComfyUI
/// est %LOCALAPPDATA%\JarvisAI\ComfyUI (installé par setup_comfyui.ps1).
/// </summary>
public sealed class ComfyUIProcessManager : IAsyncDisposable
{
    private readonly ILogger<ComfyUIProcessManager> _logger;
    private Process? _process;
    private readonly object _lock = new();
    private volatile bool _disposed;

    private static readonly string ComfyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "ComfyUI");
    private const string ApiBase = "http://127.0.0.1:8188";
    private const int ReadyTimeoutSeconds = 120;

    public bool IsRunning => _process is { HasExited: false };
    public bool IsInstalled => File.Exists(Path.Combine(ComfyDir, "run_nvidia_gpu.bat"));

    public ComfyUIProcessManager(ILogger<ComfyUIProcessManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Démarre ComfyUI si pas encore lancé. Attend qu'il soit prêt (API
    /// accessible) avant de retourner. Retourne false si indisponible.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;
        if (IsRunning && await IsApiReadyAsync(cancellationToken)) return true;

        if (!IsInstalled)
        {
            _logger.LogDebug("[ComfyUI] Non installé ({Dir})", ComfyDir);
            return false;
        }

        lock (_lock)
        {
            if (IsRunning) return true;
            try
            {
                var batPath = Path.Combine(ComfyDir, "run_nvidia_gpu.bat");
                var psi = new ProcessStartInfo
                {
                    FileName = batPath,
                    WorkingDirectory = ComfyDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                _process = Process.Start(psi);
                if (_process is null) return false;
                _logger.LogInformation("[ComfyUI] Processus démarré (PID {PID})", _process.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ComfyUI] Échec de démarrage");
                return false;
            }
        }

        // Attendre que l'API soit prête
        var deadline = DateTime.UtcNow.AddSeconds(ReadyTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsApiReadyAsync(cancellationToken))
            {
                _logger.LogInformation("[ComfyUI] API prête sur {ApiBase}", ApiBase);
                return true;
            }
            await Task.Delay(2000, cancellationToken);
        }

        _logger.LogWarning("[ComfyUI] Délai dépassé pour le démarrage");
        return false;
    }

    public async Task<bool> IsApiReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync($"{ApiBase}/system_stats", cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            if (_process is { HasExited: false })
            {
                try
                {
                    _logger.LogInformation("[ComfyUI] Arrêt du processus (PID {PID})", _process.Id);
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ComfyUI] Erreur lors de l'arrêt");
                }
            }
            _process?.Dispose();
            _process = null;
        }

        await ValueTask.CompletedTask;
    }
}
