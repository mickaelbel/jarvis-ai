using JarvisAI.Application.Abstractions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Gère le téléchargement et l'installation automatiques de ComfyUI + modèle
/// SDXL au premier lancement de Jarvis. Le processus tourne en arrière-plan
/// sans bloquer l'application : l'utilisateur peut déjà générer des images
/// via Pollinations (cloud) pendant le téléchargement. Une fois ComfyUI prêt,
/// l'AdaptiveImageGenerationService bascule automatiquement sur le moteur local.
///
/// Emplacement : %LOCALAPPDATA%\JarvisAI\ComfyUI
/// Fichier flag : %LOCALAPPDATA%\JarvisAI\comfyui-setup-done.dat
/// </summary>
public sealed class ComfyUISetupService : IAsyncDisposable
{
    private readonly ILogger<ComfyUISetupService> _logger;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private Task? _setupTask;
    private volatile bool _setupComplete;
    private volatile bool _setupFailed;

    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI");
    private static readonly string ComfyDir = Path.Combine(BaseDir, "ComfyUI");
    private static readonly string FlagFile = Path.Combine(BaseDir, "comfyui-setup-done.dat");
    private static readonly string ModelDir = Path.Combine(ComfyDir, "ComfyUI", "models", "checkpoints");
    private static readonly string LogFile = Path.Combine(BaseDir, "comfyui-setup.log");

    private const string ComfyUiUrl = "https://github.com/Comfy-Org/ComfyUI/releases/download/v0.34.0/ComfyUI_windows_portable_nvidia.7z";
    private const string ModelUrl = "https://huggingface.co/digiplay/RealVisXL_V4.0/resolve/main/RealVisXL_V4.0.safetensors";

    public bool IsSetupDone => _setupComplete || File.Exists(FlagFile);
    public bool IsSetupRunning => _setupTask is { IsCompleted: false };
    public bool IsSetupFailed => _setupFailed;

    public ComfyUISetupService(ILogger<ComfyUISetupService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>
    /// Lance le setup ComfyUI en arrière-plan si pas déjà fait. Non-bloquant :
    /// l'application continue de fonctionner avec Pollinations pendant le téléchargement.
    /// </summary>
    public void StartSetupIfNeeded()
    {
        if (IsSetupDone || IsSetupRunning) return;

        _cts = new CancellationTokenSource();
        _setupTask = RunSetupAsync(_cts.Token);
    }

    private async Task RunSetupAsync(CancellationToken ct)
    {
        Log("=== Début setup ComfyUI automatique ===");
        try
        {
            // Étape 1 : Vérifier/extraire ComfyUI
            if (!Directory.Exists(Path.Combine(ComfyDir, "ComfyUI")))
            {
                await DownloadAndExtractComfyUIAsync(ct);
            }
            else
            {
                Log("ComfyUI déjà extrait.");
            }

            // Étape 2 : Télécharger le modèle SDXL
            if (!Directory.Exists(ModelDir))
                Directory.CreateDirectory(ModelDir);

            var existingModel = Directory.GetFiles(ModelDir, "*.safetensors").FirstOrDefault();
            if (existingModel is null)
            {
                await DownloadModelAsync(ct);
            }
            else
            {
                Log($"Modèle déjà présent : {Path.GetFileName(existingModel)}");
            }

            // Étape 3 : Vérification finale
            var batFile = Path.Combine(ComfyDir, "run_nvidia_gpu.bat");
            var hasModel = Directory.GetFiles(ModelDir, "*.safetensors").Length > 0;
            if (File.Exists(batFile) && hasModel)
            {
                _setupComplete = true;
                File.WriteAllText(FlagFile, DateTime.UtcNow.ToString("o"));
                Log("=== Setup ComfyUI terminé avec succès ===");
            }
            else
            {
                _setupFailed = true;
                Log("ERREUR : Setup incomplet.");
            }
        }
        catch (OperationCanceledException)
        {
            Log("Setup annulé.");
        }
        catch (Exception ex)
        {
            _setupFailed = true;
            Log($"ERREUR setup : {ex.Message}");
            _logger.LogError(ex, "[ComfyUISetup] Échec du setup automatique");
        }
    }

    private async Task DownloadAndExtractComfyUIAsync(CancellationToken ct)
    {
        var portable7z = Path.Combine(Path.GetTempPath(), "ComfyUI_portable.7z");

        // Téléchargement
        if (!File.Exists(portable7z))
        {
            Log("Téléchargement ComfyUI portable (~2.5 Go)...");
            using var resp = await _http.GetAsync(ComfyUiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(portable7z);
            await stream.CopyToAsync(fs, ct);
            Log($"Téléchargé : {new FileInfo(portable7z).Length / 1024 / 1024} Mo");
        }
        else
        {
            Log("7z déjà téléchargé.");
        }

        // Extraction avec 7-Zip
        Directory.CreateDirectory(ComfyDir);
        var szExe = Find7Zip();
        if (szExe is null)
        {
            Log("7-Zip introuvable. Installation via winget...");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = "install --id 7zip.7zip --silent --accept-package-agreements --accept-source-agreements",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit(60000);
                szExe = Find7Zip();
            }
            catch { }
        }

        if (szExe is null)
        {
            throw new InvalidOperationException("Impossible d'installer 7-Zip automatiquement.");
        }

        Log("Extraction de ComfyUI...");
        var psi = new ProcessStartInfo
        {
            FileName = szExe,
            Arguments = $"x \"{portable7z}\" -o\"{ComfyDir}\" -y",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc is not null)
        {
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"7z a échoué (code {proc.ExitCode})");
        }

        Log("Extraction terminée.");

        // Nettoyage du 7z temporaire
        try { File.Delete(portable7z); } catch { }
    }

    private async Task DownloadModelAsync(CancellationToken ct)
    {
        Log("Téléchargement du modèle RealVisXL V4.0 (~6.6 Go)...");
        Log("Cela peut prendre plusieurs minutes selon votre connexion.");

        var modelFile = Path.Combine(ModelDir, "RealVisXL_V4.0.safetensors");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelUrl);
            request.Headers.Add("User-Agent", "JarvisAI/1.0");
            using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? 0;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(modelFile);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            var lastLog = DateTime.MinValue;

            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;

                if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(10))
                {
                    var pct = totalBytes > 0 ? (downloaded * 100 / totalBytes) : 0;
                    Log($"  {downloaded / 1024 / 1024} Mo / {totalBytes / 1024 / 1024} Mo ({pct}%)");
                    lastLog = DateTime.UtcNow;
                }
            }

            Log($"Modèle téléchargé : {new FileInfo(modelFile).Length / 1024 / 1024} Mo");
        }
        catch (Exception ex)
        {
            Log($"ERREUR téléchargement modèle : {ex.Message}");
            throw;
        }
    }

    private static string? Find7Zip()
    {
        var candidates = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _logger.LogInformation("[ComfyUISetup] {Msg}", msg);
        try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_setupTask is not null)
        {
            try { await _setupTask; } catch { }
        }
        _cts?.Dispose();
        _http?.Dispose();
    }
}
