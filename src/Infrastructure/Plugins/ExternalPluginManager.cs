using JarvisAI.Application.Plugins;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Plugins;

public sealed class ExternalPluginManager : IExternalPluginManager
{
    private readonly ILogger<ExternalPluginManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _pluginsDir;

    // Registry of available external plugins
    private readonly List<ExternalPlugin> _registry = new()
    {
        new ExternalPlugin
        {
            Id = "blender-jarvis",
            Name = "JarvisAI Blender",
            Description = "Contrôle complet de Blender depuis JarvisAI. Scènes, objets, caméras, matériaux, rendus, animation, scripts Python.",
            Version = "1.0.0",
            Author = "JarvisAI",
            DownloadUrl = "https://raw.githubusercontent.com/jarvisai/plugins/main/blender/jarvisai_blender.py",
            FileName = "jarvisai_blender.py",
            Type = ExternalPluginType.BlenderAddon,
            TargetApplication = "Blender",
            SupportedVersions = new() // rempli dynamiquement
        }
    };

    public ExternalPluginManager(ILogger<ExternalPluginManager> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _pluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExternalPlugins");
        Directory.CreateDirectory(_pluginsDir);

        // Check installed status on startup
        _ = RefreshStatusesAsync();
    }

    public Task<IReadOnlyList<ExternalPlugin>> GetPluginsAsync()
    {
        return Task.FromResult<IReadOnlyList<ExternalPlugin>>(_registry.AsReadOnly());
    }

    public Task<ExternalPlugin?> GetPluginAsync(string pluginId)
    {
        return Task.FromResult(_registry.FirstOrDefault(p => p.Id == pluginId));
    }

    public async Task<bool> InstallAsync(string pluginId, string targetVersion, CancellationToken ct = default)
    {
        var plugin = _registry.FirstOrDefault(p => p.Id == pluginId);
        if (plugin is null) return false;

        try
        {
            plugin.Status = ExternalPluginStatus.Installing;

            // Find Blender addons directory (direct path, no Blender process)
            var addonsDir = GetBlenderAddonsDirDirect(targetVersion);
            if (string.IsNullOrEmpty(addonsDir))
            {
                plugin.Status = ExternalPluginStatus.Error;
                return false;
            }

            Directory.CreateDirectory(addonsDir);

            // Find the bundled addon file
            var localAddon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "BlenderAddon", plugin.FileName);
            if (!File.Exists(localAddon))
            {
                plugin.Status = ExternalPluginStatus.Error;
                return false;
            }

            // Copy to Blender addons directory
            var destPath = Path.Combine(addonsDir, plugin.FileName);
            File.Copy(localAddon, destPath, true);

            // Enable the addon
            var blenderPath = FindBlenderPath(targetVersion);
            if (!string.IsNullOrEmpty(blenderPath))
            {
                await EnableAddonAsync(blenderPath, plugin.FileName, ct);
            }

            plugin.Status = ExternalPluginStatus.Installed;
            plugin.InstalledPath = destPath;
            plugin.InstalledVersion = targetVersion;

            return true;
        }
        catch (Exception ex)
        {
            plugin.Status = ExternalPluginStatus.Error;
            _logger.LogError(ex, "[ExternalPlugin] Failed to install {Plugin}", pluginId);
            return false;
        }
    }

    public async Task<bool> UninstallAsync(string pluginId, CancellationToken ct = default)
    {
        var plugin = _registry.FirstOrDefault(p => p.Id == pluginId);
        if (plugin is null) return false;

        try
        {
            if (!string.IsNullOrEmpty(plugin.InstalledPath) && File.Exists(plugin.InstalledPath))
            {
                File.Delete(plugin.InstalledPath);
            }

            plugin.Status = ExternalPluginStatus.NotInstalled;
            plugin.InstalledPath = null;
            plugin.InstalledVersion = null;

            _logger.LogInformation("[ExternalPlugin] Uninstalled {Plugin}", pluginId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ExternalPlugin] Failed to uninstall {Plugin}", pluginId);
            return false;
        }
    }

    public Task<bool> UpdateAsync(string pluginId, CancellationToken ct = default)
    {
        // For now, reinstall = update
        return InstallAsync(pluginId, "latest", ct);
    }

    public Task<ExternalPluginStatus> GetStatusAsync(string pluginId)
    {
        var plugin = _registry.FirstOrDefault(p => p.Id == pluginId);
        return Task.FromResult(plugin?.Status ?? ExternalPluginStatus.NotInstalled);
    }

    public async Task<List<string>> DetectTargetVersionsAsync(string pluginId)
    {
        var plugin = _registry.FirstOrDefault(p => p.Id == pluginId);
        if (plugin?.Type == ExternalPluginType.BlenderAddon)
        {
            return await GetInstalledBlenderVersionsAsync();
        }
        return new List<string>();
    }

    private async Task RefreshStatusesAsync()
    {
        foreach (var plugin in _registry)
        {
            if (plugin.Type == ExternalPluginType.BlenderAddon)
            {
                var versions = await GetInstalledBlenderVersionsAsync();
                plugin.SupportedVersions = versions;

                if (versions.Count == 0)
                {
                    plugin.Status = ExternalPluginStatus.NotInstalled;
                    continue;
                }

                // For each installed Blender version, check if addon exists
                foreach (var ver in versions)
                {
                    var addonsDir = GetBlenderAddonsDirDirect(ver);
                    if (!string.IsNullOrEmpty(addonsDir))
                    {
                        var path = Path.Combine(addonsDir, plugin.FileName);
                        if (File.Exists(path))
                        {
                            plugin.Status = ExternalPluginStatus.Installed;
                            plugin.InstalledPath = path;
                            plugin.InstalledVersion = ver;
                            return;
                        }
                    }
                }

                plugin.Status = ExternalPluginStatus.NotInstalled;
            }
        }
    }

    private async Task<List<string>> GetInstalledBlenderVersionsAsync()
    {
        var versions = new List<string>();
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
                        versions.Add(version);
                    }
                }
            }
        }

        return versions;
    }

    private async Task<string?> GetBlenderAddonsDirAsync(string blenderVersion, CancellationToken ct = default)
    {
        // Direct path without running Blender
        return GetBlenderAddonsDirDirect(blenderVersion);
    }

    private static string? GetBlenderAddonsDirDirect(string blenderVersion)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(userProfile, "Blender Foundation", "Blender", blenderVersion, "scripts", "addons");
    }

    private async Task EnableAddonAsync(string blenderPath, string addonFileName, CancellationToken ct = default)
    {
        var moduleName = Path.GetFileNameWithoutExtension(addonFileName);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = blenderPath,
                Arguments = $"--background --python-expr \"import bpy; bpy.ops.preferences.addon_enable(module='{moduleName}'); bpy.ops.wm.save_userpref()\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
                await process.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ExternalPlugin] Failed to enable addon");
        }
    }

    private static string FindBlenderPath(string version)
    {
        var paths = new[]
        {
            $@"C:\Program Files\Blender Foundation\Blender {version}\blender.exe",
            $@"C:\Program Files (x86)\Blender Foundation\Blender {version}\blender.exe",
        };

        foreach (var path in paths)
            if (File.Exists(path)) return path;

        return "";
    }
}
