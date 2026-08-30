using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface INotificationRoutingService
{
    void ConfigureRoute(string eventType, NotificationChannel channel, string? target = null);
    IReadOnlyList<NotificationRoute> GetRoutes();
    void DeleteRoute(string routeId);
    NotificationResult RouteNotification(string eventType, string title, string message, Dictionary<string, string>? properties = null);
    IReadOnlyList<NotificationLog> GetLog(int hours = 24);
    void AddChannel(string name, NotificationChannel channel, string configuration);
    IReadOnlyList<NotificationChannelConfig> GetChannels();
}

public sealed class NotificationRoutingService : INotificationRoutingService
{
    private readonly ILogger<NotificationRoutingService> _logger;
    private readonly string _storagePath;
    private readonly List<NotificationRoute> _routes = new();
    private readonly List<NotificationChannelConfig> _channels = new();
    private readonly List<NotificationLog> _logs = new();

    public NotificationRoutingService(ILogger<NotificationRoutingService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "notification_routing.json");
        Load();
    }

    public void ConfigureRoute(string eventType, NotificationChannel channel, string? target = null)
    {
        var route = new NotificationRoute
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            EventType = eventType,
            Channel = channel,
            Target = target,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _routes.Add(route);
        Save();
    }

    public IReadOnlyList<NotificationRoute> GetRoutes()
        => _routes;

    public void DeleteRoute(string routeId)
    {
        _routes.RemoveAll(r => r.Id == routeId);
        Save();
    }

    public NotificationResult RouteNotification(string eventType, string title, string message, Dictionary<string, string>? properties = null)
    {
        var matchingRoutes = _routes.Where(r =>
            r.IsEnabled &&
            (r.EventType == "*" || r.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (!matchingRoutes.Any())
        {
            return new NotificationResult
            {
                Success = false,
                Message = "Aucune route configurée pour cet événement"
            };
        }

        var results = new List<string>();
        foreach (var route in matchingRoutes)
        {
            // Simulate sending
            results.Add($"Envoyé via {route.Channel}");

            _logs.Add(new NotificationLog
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                EventType = eventType,
                Title = title,
                Message = message,
                Channel = route.Channel,
                Target = route.Target ?? "",
                SentAt = DateTime.UtcNow
            });
        }

        Save();

        return new NotificationResult
        {
            Success = true,
            Message = $"Notification envoyée via {results.Count} canal(aux)",
            ChannelsUsed = matchingRoutes.Select(r => r.Channel).ToList()
        };
    }

    public IReadOnlyList<NotificationLog> GetLog(int hours = 24)
    {
        var cutoff = DateTime.UtcNow.AddHours(-hours);
        return _logs.Where(l => l.SentAt >= cutoff)
                   .OrderByDescending(l => l.SentAt)
                   .ToList();
    }

    public void AddChannel(string name, NotificationChannel channel, string configuration)
    {
        var config = new NotificationChannelConfig
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Channel = channel,
            Configuration = configuration,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _channels.Add(config);
        Save();
    }

    public IReadOnlyList<NotificationChannelConfig> GetChannels()
        => _channels;

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("routes", out var routesEl))
                {
                    var loaded = JsonSerializer.Deserialize<List<NotificationRoute>>(routesEl.GetRawText());
                    if (loaded is not null) _routes.AddRange(loaded);
                }

                if (root.TryGetProperty("channels", out var channelsEl))
                {
                    var loaded = JsonSerializer.Deserialize<List<NotificationChannelConfig>>(channelsEl.GetRawText());
                    if (loaded is not null) _channels.AddRange(loaded);
                }

                if (root.TryGetProperty("logs", out var logsEl))
                {
                    var loaded = JsonSerializer.Deserialize<List<NotificationLog>>(logsEl.GetRawText());
                    if (loaded is not null) _logs.AddRange(loaded);
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

            // Keep only last 1000 logs
            if (_logs.Count > 1000)
                _logs.RemoveRange(0, _logs.Count - 1000);

            var data = new { routes = _routes, channels = _channels, logs = _logs };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class NotificationRoute
{
    public string Id { get; set; } = "";
    public string EventType { get; set; } = "";
    public NotificationChannel Channel { get; set; }
    public string? Target { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class NotificationChannelConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public NotificationChannel Channel { get; set; }
    public string Configuration { get; set; } = "";
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class NotificationLog
{
    public string Id { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public NotificationChannel Channel { get; set; }
    public string Target { get; set; } = "";
    public DateTime SentAt { get; set; }
}

public sealed class NotificationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<NotificationChannel> ChannelsUsed { get; set; } = new();
}

public enum NotificationChannel { InApp, Email, Slack, Discord, Webhook, SMS }
