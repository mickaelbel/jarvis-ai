using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Démarre automatiquement les serveurs Python (image Qwen-Image + vidéo AnimateDiff)
/// en arrière-plan au lancement de l'application. Les serveurs tournent en daemon
/// et s'arrêtent proprement à la fermeture.
/// </summary>
public sealed class PythonServerHostedService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<PythonServerHostedService> _logger;
    private Process? _imageServer;
    private Process? _videoServer;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    private static readonly string ScriptsDir = FindScriptsDir();

    public PythonServerHostedService(ILogger<PythonServerHostedService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Lancer les serveurs en arrière-plan, non-bloquant
        _ = Task.Run(() => StartServersAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task StartServersAsync(CancellationToken ct)
    {
        // Attendre un peu que l'app soit prête
        try { await Task.Delay(2000, ct); } catch { return; }

        // Serveur image (Qwen-Image, port 8189)
        var imageScript = Path.Combine(ScriptsDir, "qwen_image_server.py");
        if (File.Exists(imageScript))
        {
            _logger.LogInformation("[PythonServers] Démarrage du serveur image (Qwen-Image)...");
            _imageServer = StartPythonScript(imageScript, "QwenImage");
        }
        else
        {
            _logger.LogWarning("[PythonServers] Script image introuvable : {Script}", imageScript);
        }

        // Serveur vidéo (AnimateDiff, port 8190)
        var videoScript = Path.Combine(ScriptsDir, "local_video_server.py");
        if (File.Exists(videoScript))
        {
            _logger.LogInformation("[PythonServers] Démarrage du serveur vidéo (AnimateDiff)...");
            _videoServer = StartPythonScript(videoScript, "LocalVideo");
        }
        else
        {
            _logger.LogWarning("[PythonServers] Script vidéo introuvable : {Script}", videoScript);
        }

        // Attendre que les serveurs soient prêts (max 180s pour le téléchargement des modèles)
        _logger.LogInformation("[PythonServers] En attente du chargement des modèles (~1-3 min au 1er lancement)...");
    }

    private Process? StartPythonScript(string scriptPath, string label)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? ""
            };

            var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogWarning("[PythonServers] Impossible de démarrer {Label}", label);
                return null;
            }

            _logger.LogInformation("[PythonServers] {Label} démarré (PID {PID})", label, process.Id);

            // Logger la sortie en arrière-plan
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardOutput.EndOfStream)
                    {
                        var line = await process.StandardOutput.ReadLineAsync();
                        if (!string.IsNullOrEmpty(line))
                            _logger.LogDebug("[{Label}] {Line}", label, line);
                    }
                }
                catch { }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardError.EndOfStream)
                    {
                        var line = await process.StandardError.ReadLineAsync();
                        if (!string.IsNullOrEmpty(line))
                            _logger.LogWarning("[{Label}] {Line}", label, line);
                    }
                }
                catch { }
            });

            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PythonServers] Échec de démarrage de {Label}", label);
            return null;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        KillServer(ref _imageServer, "QwenImage");
        KillServer(ref _videoServer, "LocalVideo");
        return Task.CompletedTask;
    }

    private void KillServer(ref Process? process, string label)
    {
        lock (_lock)
        {
            if (process is { HasExited: false })
            {
                try
                {
                    _logger.LogInformation("[PythonServers] Arrêt de {Label} (PID {PID})", label, process.Id);
                    process.Kill(entireProcessTree: true);
                }
                catch { }
            }
            process?.Dispose();
            process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        KillServer(ref _imageServer, "QwenImage");
        KillServer(ref _videoServer, "LocalVideo");
        await ValueTask.CompletedTask;
    }

    private static string FindScriptsDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "scripts"),
            Path.Combine(baseDir, "..", "..", "..", "scripts"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", "scripts"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (Directory.Exists(full)) return full;
        }
        return Path.GetFullPath(candidates[0]);
    }
}
