using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface IPluginInstallerService
{
    Task<InstallResult> InstallBlenderAddonAsync(string? blenderPath = null, CancellationToken ct = default);
    Task<InstallResult> UninstallBlenderAddonAsync(string? blenderPath = null, CancellationToken ct = default);
    Task<List<BlenderVersion>> GetInstalledBlenderVersionsAsync();
    Task<bool> IsBlenderAddonInstalledAsync(string? blenderPath = null);
    Task<AddonStatus> GetAddonStatusAsync(string? blenderPath = null);
}

public sealed class PluginInstallerService : IPluginInstallerService
{
    private readonly ILogger<PluginInstallerService> _logger;
    private readonly string _addonSourcePath;

    public PluginInstallerService(ILogger<PluginInstallerService> logger)
    {
        _logger = logger;
        _addonSourcePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Plugins", "BlenderAddon", "jarvisai_blender.py");
    }

    public async Task<InstallResult> InstallBlenderAddonAsync(string? blenderPath = null, CancellationToken ct = default)
    {
        var result = new InstallResult { Operation = "Install Blender Addon" };

        var blender = blenderPath ?? FindBlender();
        if (string.IsNullOrEmpty(blender))
        {
            result.ErrorMessage = "Blender non trouvé. Installe Blender depuis https://www.blender.org/download/";
            return result;
        }

        try
        {
            // Trouver le dossier addons de Blender
            var addonsDir = await GetBlenderAddonsDirAsync(blender, ct);
            if (string.IsNullOrEmpty(addonsDir))
            {
                result.ErrorMessage = "Impossible de trouver le dossier addons de Blender";
                return result;
            }

            // Créer le dossier si nécessaire
            Directory.CreateDirectory(addonsDir);

            // Copier l'addon
            var destPath = Path.Combine(addonsDir, "jarvisai_blender.py");

            if (!File.Exists(_addonSourcePath))
            {
                result.ErrorMessage = $"Addon source non trouvé : {_addonSourcePath}";
                return result;
            }

            File.Copy(_addonSourcePath, destPath, true);

            // Activer l'addon via Python
            await EnableAddonAsync(blender, ct);

            result.Success = true;
            result.InstallPath = destPath;
            result.BlenderPath = blender;
            _logger.LogInformation("[PluginInstaller] Addon installé : {Path}", destPath);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[PluginInstaller] Échec installation");
        }

        return result;
    }

    public async Task<InstallResult> UninstallBlenderAddonAsync(string? blenderPath = null, CancellationToken ct = default)
    {
        var result = new InstallResult { Operation = "Uninstall Blender Addon" };

        var blender = blenderPath ?? FindBlender();
        if (string.IsNullOrEmpty(blender))
        {
            result.ErrorMessage = "Blender non trouvé";
            return result;
        }

        try
        {
            var addonsDir = await GetBlenderAddonsDirAsync(blender, ct);
            if (!string.IsNullOrEmpty(addonsDir))
            {
                var addonPath = Path.Combine(addonsDir, "jarvisai_blender.py");
                if (File.Exists(addonPath))
                {
                    File.Delete(addonPath);
                    result.Success = true;
                    _logger.LogInformation("[PluginInstaller] Addon supprimé");
                }
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public Task<List<BlenderVersion>> GetInstalledBlenderVersionsAsync()
    {
        var versions = new List<BlenderVersion>();
        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var pf in programFiles)
        {
            var blenderDir = Path.Combine(pf, "Blender Foundation");
            if (Directory.Exists(blenderDir))
            {
                foreach (var verDir in Directory.GetDirectories(blenderDir))
                {
                    var exePath = Path.Combine(verDir, "blender.exe");
                    if (File.Exists(exePath))
                    {
                        var version = Path.GetFileName(verDir);
                        versions.Add(new BlenderVersion
                        {
                            Version = version,
                            Path = exePath,
                            IsInstalled = true
                        });
                    }
                }
            }
        }

        return Task.FromResult(versions);
    }

    public async Task<bool> IsBlenderAddonInstalledAsync(string? blenderPath = null)
    {
        var blender = blenderPath ?? FindBlender();
        if (string.IsNullOrEmpty(blender)) return false;

        var addonsDir = await GetBlenderAddonsDirAsync(blender);
        if (string.IsNullOrEmpty(addonsDir)) return false;

        return File.Exists(Path.Combine(addonsDir, "jarvisai_blender.py"));
    }

    public async Task<AddonStatus> GetAddonStatusAsync(string? blenderPath = null)
    {
        var status = new AddonStatus
        {
            IsInstalled = await IsBlenderAddonInstalledAsync(blenderPath),
            BlenderPath = blenderPath ?? FindBlender()
        };

        if (status.IsInstalled)
        {
            // Vérifier si le serveur est accessible
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await client.GetAsync("http://127.0.0.1:7777/health");
                status.ServerRunning = response.IsSuccessStatusCode;
            }
            catch
            {
                status.ServerRunning = false;
            }
        }

        return status;
    }

    private async Task<string?> GetBlenderAddonsDirAsync(string blenderPath, CancellationToken ct = default)
    {
        try
        {
            // Exécuter Blender en mode Python pour obtenir le chemin des addons
            var psi = new ProcessStartInfo
            {
                FileName = blenderPath,
                Arguments = "--background --python-expr \"import bpy; import os; print(os.path.join(bpy.utils.user_resource('SCRIPTS'), 'addons'))\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                // Parser la sortie pour trouver le chemin
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Contains("addons") && (trimmed.Contains("/") || trimmed.Contains("\\")))
                    {
                        return trimmed;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PluginInstaller] Failed to get addons dir");
        }

        // Fallback : chemin par défaut
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(userProfile, "Blender Foundation", "Blender", "4.2", "scripts", "addons");
    }

    private async Task EnableAddonAsync(string blenderPath, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = blenderPath,
                Arguments = "--background --python-expr \"import bpy; bpy.ops.preferences.addon_enable(module='jarvisai_blender'); bpy.ops.wm.save_userpref()\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
                await process.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PluginInstaller] Failed to enable addon");
        }
    }

    private static string FindBlender()
    {
        var paths = new[]
        {
            @"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 5.0\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.1\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.0\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 3.6\blender.exe",
        };

        foreach (var path in paths)
            if (File.Exists(path)) return path;

        return "";
    }
}

public sealed class InstallResult
{
    public bool Success { get; set; }
    public string Operation { get; set; } = "";
    public string? InstallPath { get; set; }
    public string? BlenderPath { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class BlenderVersion
{
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsInstalled { get; set; }
}

public sealed class AddonStatus
{
    public bool IsInstalled { get; set; }
    public bool ServerRunning { get; set; }
    public string? BlenderPath { get; set; }
}
