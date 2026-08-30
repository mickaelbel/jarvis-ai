using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IPluginMarketplace
{
    IReadOnlyList<MarketplacePlugin> GetAvailablePlugins();
    IReadOnlyList<InstalledPlugin> GetInstalledPlugins();
    Task<bool> InstallPluginAsync(string pluginId, CancellationToken ct = default);
    Task<bool> UninstallPluginAsync(string pluginId, CancellationToken ct = default);
    Task<bool> UpdatePluginAsync(string pluginId, CancellationToken ct = default);
    void EnablePlugin(string pluginId);
    void DisablePlugin(string pluginId);
    Task RefreshCatalogAsync(CancellationToken ct = default);
}

public sealed class PluginMarketplace : IPluginMarketplace
{
    private readonly ILogger<PluginMarketplace> _logger;
    private readonly string _storagePath;
    private readonly string _pluginsDir;
    private List<MarketplacePlugin> _catalog = new();
    private List<InstalledPlugin> _installed = new();

    public PluginMarketplace(ILogger<PluginMarketplace> logger)
    {
        _logger = logger;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _storagePath = Path.Combine(appData, "JarvisAI", "plugin_marketplace.json");
        _pluginsDir = Path.Combine(appData, "JarvisAI", "plugins");
        Directory.CreateDirectory(_pluginsDir);
        Load();
        if (_catalog.Count == 0) _ = RefreshCatalogAsync();
    }

    public IReadOnlyList<MarketplacePlugin> GetAvailablePlugins()
        => _catalog.Where(p => !_installed.Any(i => i.Id == p.Id)).ToList();

    public IReadOnlyList<InstalledPlugin> GetInstalledPlugins() => _installed.ToList();

    public async Task<bool> InstallPluginAsync(string pluginId, CancellationToken ct = default)
    {
        var plugin = _catalog.FirstOrDefault(p => p.Id == pluginId);
        if (plugin is null) return false;

        var installed = new InstalledPlugin
        {
            Id = plugin.Id,
            Name = plugin.Name,
            Description = plugin.Description,
            Version = plugin.Version,
            Author = plugin.Author,
            IsEnabled = true,
            InstalledAt = DateTime.UtcNow
        };

        _installed.Add(installed);
        Save();
        _logger.LogInformation("[PluginMarketplace] Installed: {Name} v{Version}", plugin.Name, plugin.Version);
        return true;
    }

    public async Task<bool> UninstallPluginAsync(string pluginId, CancellationToken ct = default)
    {
        var removed = _installed.RemoveAll(p => p.Id == pluginId);
        if (removed > 0)
        {
            Save();
            _logger.LogInformation("[PluginMarketplace] Uninstalled: {Id}", pluginId);
        }
        return removed > 0;
    }

    public async Task<bool> UpdatePluginAsync(string pluginId, CancellationToken ct = default)
    {
        var installed = _installed.FirstOrDefault(p => p.Id == pluginId);
        var catalog = _catalog.FirstOrDefault(p => p.Id == pluginId);
        if (installed is null || catalog is null) return false;

        installed.Version = catalog.Version;
        Save();
        return true;
    }

    public void EnablePlugin(string pluginId)
    {
        var plugin = _installed.FirstOrDefault(p => p.Id == pluginId);
        if (plugin is not null) { plugin.IsEnabled = true; Save(); }
    }

    public void DisablePlugin(string pluginId)
    {
        var plugin = _installed.FirstOrDefault(p => p.Id == pluginId);
        if (plugin is not null) { plugin.IsEnabled = false; Save(); }
    }

    public async Task RefreshCatalogAsync(CancellationToken ct = default)
    {
        _catalog = new List<MarketplacePlugin>
        {
            new() { Id = "slack", Name = "Slack Integration", Description = "Notifications et commandes Slack", Version = "1.0.0", Author = "Community", Category = "Messaging", Downloads = 1250, Rating = 4.5 },
            new() { Id = "homeassistant", Name = "Home Assistant", Description = "Contrôle domotique complet via Home Assistant API", Version = "1.2.0", Author = "Community", Category = "IoT", Downloads = 890, Rating = 4.7 },
            new() { Id = "github", Name = "GitHub", Description = "Gestion de repositories, issues et pull requests", Version = "2.0.0", Author = "Official", Category = "Development", Downloads = 2100, Rating = 4.8 },
            new() { Id = "trello", Name = "Trello Boards", Description = "Gestion de tableaux et cartes Trello", Version = "1.0.0", Author = "Community", Category = "Productivity", Downloads = 670, Rating = 4.3 },
            new() { Id = "notion", Name = "Notion", Description = "Lire et écrire dans les bases Notion", Version = "1.1.0", Author = "Community", Category = "Productivity", Downloads = 540, Rating = 4.2 },
            new() { Id = "spotify", Name = "Spotify Control", Description = "Contrôle musical Spotify: lecture, playlists, recherche", Version = "1.0.0", Author = "Community", Category = "Media", Downloads = 1800, Rating = 4.6 },
            new() { Id = "obsidian", Name = "Obsidian Sync", Description = "Synchroniser les notes avec Obsidian", Version = "0.9.0", Author = "Community", Category = "Productivity", Downloads = 420, Rating = 4.1 },
            new() { Id = "docker", Name = "Docker Manager", Description = "Gérer les containers et images Docker", Version = "1.0.0", Author = "Official", Category = "Development", Downloads = 780, Rating = 4.4 },
        };

        _logger.LogInformation("[PluginMarketplace] Catalog refreshed: {Count} plugins", _catalog.Count);
        await Task.CompletedTask;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("installed", out var installedEl))
                {
                    var installedJson = installedEl.GetRawText();
                    _installed = JsonSerializer.Deserialize<List<InstalledPlugin>>(installedJson) ?? new();
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new { installed = _installed };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class MarketplacePlugin
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Category { get; set; } = "";
    public int Downloads { get; set; }
    public double Rating { get; set; }
}

public sealed class InstalledPlugin
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public bool IsEnabled { get; set; }
    public DateTime InstalledAt { get; set; }
}
