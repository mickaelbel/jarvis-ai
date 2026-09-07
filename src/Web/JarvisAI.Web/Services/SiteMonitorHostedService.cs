using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public sealed class MonitoredSitesConfig
{
    public int IntervalSeconds { get; set; } = 300;
    public List<string> Urls { get; set; } = new();
}

public static class MonitoredSitesConfigStore
{
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "config", "monitored_sites.json");

    public static MonitoredSitesConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<MonitoredSitesConfig>(File.ReadAllText(ConfigPath));
                if (loaded is not null)
                {
                    loaded.Urls ??= new List<string>();
                    if (loaded.IntervalSeconds <= 0) loaded.IntervalSeconds = 300;
                    return loaded;
                }
            }
        }
        catch
        {
        }
        return new MonitoredSitesConfig();
    }

    public static void Save(MonitoredSitesConfig config)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class SiteMonitorHostedService : BackgroundService
{
    private readonly ISiteMonitorService _monitor;
    private readonly INotificationRoutingService _notifier;
    private readonly ILogAggregationService _logs;
    private readonly ILogger<SiteMonitorHostedService> _logger;
    private readonly HashSet<string> _downSites = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public SiteMonitorHostedService(
        ISiteMonitorService monitor,
        INotificationRoutingService notifier,
        ILogAggregationService logs,
        ILogger<SiteMonitorHostedService> logger)
    {
        _monitor = monitor;
        _notifier = notifier;
        _logs = logs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = MonitoredSitesConfigStore.Load();
                var interval = config.IntervalSeconds > 0 ? config.IntervalSeconds : 300;

                foreach (var url in config.Urls)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await CheckSiteAsync(url, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SiteMonitor] Erreur de boucle");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task CheckSiteAsync(string url, CancellationToken ct)
    {
        var status = await _monitor.CheckSiteAsync(url, ct);
        lock (_lock)
        {
            var wasDown = _downSites.Contains(url);
            if (!status.IsSuccess)
            {
                _downSites.Add(url);
                var message = $"Site en panne: {url} ({status.ErrorMessage ?? status.StatusCode.ToString()})";
                var props = new Dictionary<string, string>
                {
                    ["url"] = url,
                    ["code"] = status.StatusCode.ToString(),
                    ["error"] = status.ErrorMessage ?? ""
                };
                _logs.AddLog("SiteMonitor", LogLevel.Error, message, props);
                _notifier.RouteNotification("site_down", "Site indisponible", message, props);
                _logger.LogWarning("[SiteMonitor] {Message}", message);
            }
            else if (wasDown)
            {
                _downSites.Remove(url);
                var message = $"Site de nouveau disponible: {url}";
                _notifier.RouteNotification("site_recovered", "Site rétabli", message, new Dictionary<string, string> { ["url"] = url });
                _logs.AddLog("SiteMonitor", LogLevel.Information, message);
                _logger.LogInformation("[SiteMonitor] {Message}", message);
            }
        }
    }
}