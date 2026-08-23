using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.AI;

/// <summary>
/// Démarre le serveur Ollama local s'il n'est pas déjà joignable et attend
/// qu'il soit prêt. Utilisé au démarrage de l'app et à la demande quand une
/// requête IA trouve Ollama arrêté.
/// </summary>
public sealed class OllamaLauncher
{
    private const string VersionUrl = "http://localhost:11434/api/version";
    private readonly ILogger<OllamaLauncher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _probeClient;
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRunningUtc = DateTimeOffset.MinValue;

    public OllamaLauncher(ILogger<OllamaLauncher> logger)
    {
        _logger = logger;
        _probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _probeClient.GetAsync(VersionUrl, ct);
            var ok = resp.IsSuccessStatusCode;
            if (ok) _lastRunningUtc = DateTimeOffset.UtcNow;
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOllamaProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("ollama").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> EnsureRunningAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (await IsRunningAsync(ct)) return true;

        await _gate.WaitAsync(ct);
        try
        {
            if (await IsRunningAsync(ct)) return true;

        var now = DateTimeOffset.UtcNow;
        var recentlyLaunched = now - _lastAttemptUtc < TimeSpan.FromSeconds(15);
        var wasRecentlyUp = now - _lastRunningUtc < TimeSpan.FromSeconds(60);
        if (recentlyLaunched && wasRecentlyUp)
        {
            _logger.LogDebug("[Ollama] Démarrage déjà tenté récemment, on attend la disponibilité");
            return await WaitUntilReadyAsync(timeout, ct);
        }

        if (IsOllamaProcessRunning())
        {
            // Ollama est lancé mais le serveur ne répond pas encore (chargement de
            // modèle, machine surchargée) : on attend sans relancer une seconde instance.
            _logger.LogInformation("[Ollama] Process ollama présent mais serveur muet; attente de disponibilité");
            WriteLog("process ollama présent mais serveur muet ; attente");
            return await WaitUntilReadyAsync(timeout ?? TimeSpan.FromSeconds(45), ct);
        }

        _lastAttemptUtc = DateTimeOffset.UtcNow;

            var exe = FindOllamaExe();
            if (exe is null)
            {
                _logger.LogWarning("[Ollama] ollama.exe introuvable (aucune installation détectée)");
                WriteLog("ollama.exe introuvable");
                return false;
            }

            _logger.LogInformation("[Ollama] Ollama non joignable, démarrage de '{Exe}' serve", exe);
            WriteLog($"démarrage de {exe} serve");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "serve",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Le dossier des modèles est défini dans les paramètres (variable
                // d'environnement utilisateur OLLAMA_MODELS) : on l'injecte pour
                // que Ollama télécharge les modèles au bon endroit.
                var modelsPath = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);
                if (string.IsNullOrWhiteSpace(modelsPath))
                    modelsPath = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
                if (!string.IsNullOrWhiteSpace(modelsPath))
                {
                    psi.Environment["OLLAMA_MODELS"] = modelsPath;
                    _logger.LogInformation("[Ollama] OLLAMA_MODELS={Path}", modelsPath);
                }

                Process.Start(psi);
                WriteLog("processus lancé");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Ollama] Échec du lancement de '{Exe}'", exe);
                WriteLog($"échec du lancement : {ex.Message}");
                return false;
            }

            return await WaitUntilReadyAsync(timeout, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? LogPath()
    {
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "JarvisAI", "ollama-launcher.log");
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            var path = LogPath();
            if (path is null) return;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Le journal ne doit jamais empêcher le démarrage.
        }
    }

    private async Task<bool> WaitUntilReadyAsync(TimeSpan? timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(25));
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await IsRunningAsync(ct)) return true;
            await Task.Delay(500, ct);
        }
        _logger.LogWarning("[Ollama] Ollama pas encore prêt après le démarrage");
        WriteLog("Ollama pas encore prêt après le démarrage");
        return false;
    }

    private static string? FindOllamaExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Ollama", "ollama.exe")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';');
        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "ollama.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // chemin invalide, on continue
            }
        }
        return null;
    }
}
