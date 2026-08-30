using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ISessionAnalyticsService
{
    void TrackEvent(string eventName, Dictionary<string, string>? properties = null);
    IReadOnlyList<AnalyticsEvent> GetEvents(DateTime? from = null, DateTime? to = null);
    AnalyticsSummary GetSummary(int days = 30);
    IReadOnlyList<TopItem> GetTopCommands(int limit = 10);
    IReadOnlyList<TopItem> GetTopTools(int limit = 10);
    IReadOnlyList<DailyActivity> GetDailyActivity(int days = 30);
}

public sealed class SessionAnalyticsService : ISessionAnalyticsService
{
    private readonly ILogger<SessionAnalyticsService> _logger;
    private readonly string _storagePath;
    private readonly List<AnalyticsEvent> _events = new();

    public SessionAnalyticsService(ILogger<SessionAnalyticsService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "analytics.json");
        Load();
    }

    public void TrackEvent(string eventName, Dictionary<string, string>? properties = null)
    {
        var evt = new AnalyticsEvent
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            EventName = eventName,
            Properties = properties ?? new(),
            Timestamp = DateTime.UtcNow
        };

        _events.Add(evt);

        // Keep only last 10000 events
        if (_events.Count > 10000)
        {
            var toRemove = _events.Count - 10000;
            _events.RemoveRange(0, toRemove);
        }

        Save();
    }

    public IReadOnlyList<AnalyticsEvent> GetEvents(DateTime? from = null, DateTime? to = null)
    {
        var query = _events.AsEnumerable();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        return query.ToList();
    }

    public AnalyticsSummary GetSummary(int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var recent = _events.Where(e => e.Timestamp >= cutoff).ToList();

        return new AnalyticsSummary
        {
            TotalEvents = recent.Count,
            UniqueDays = recent.Select(e => e.Timestamp.Date).Distinct().Count(),
            TotalCommands = recent.Count(e => e.EventName == "command"),
            TotalToolCalls = recent.Count(e => e.EventName == "tool_call"),
            TotalErrors = recent.Count(e => e.EventName == "error"),
            PeriodDays = days
        };
    }

    public IReadOnlyList<TopItem> GetTopCommands(int limit = 10)
    {
        return _events
            .Where(e => e.EventName == "command" && e.Properties.ContainsKey("command"))
            .GroupBy(e => e.Properties["command"])
            .OrderByDescending(g => g.Count())
            .Take(limit)
            .Select(g => new TopItem { Name = g.Key, Count = g.Count() })
            .ToList();
    }

    public IReadOnlyList<TopItem> GetTopTools(int limit = 10)
    {
        return _events
            .Where(e => e.EventName == "tool_call" && e.Properties.ContainsKey("tool"))
            .GroupBy(e => e.Properties["tool"])
            .OrderByDescending(g => g.Count())
            .Take(limit)
            .Select(g => new TopItem { Name = g.Key, Count = g.Count() })
            .ToList();
    }

    public IReadOnlyList<DailyActivity> GetDailyActivity(int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return _events
            .Where(e => e.Timestamp >= cutoff)
            .GroupBy(e => e.Timestamp.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailyActivity
            {
                Date = g.Key,
                EventCount = g.Count(),
                CommandCount = g.Count(e => e.EventName == "command"),
                ToolCallCount = g.Count(e => e.EventName == "tool_call")
            })
            .ToList();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<AnalyticsEvent>>(json);
                if (loaded is not null) _events.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(_events, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class AnalyticsEvent
{
    public string Id { get; set; } = "";
    public string EventName { get; set; } = "";
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTime Timestamp { get; set; }
}

public sealed class AnalyticsSummary
{
    public int TotalEvents { get; set; }
    public int UniqueDays { get; set; }
    public int TotalCommands { get; set; }
    public int TotalToolCalls { get; set; }
    public int TotalErrors { get; set; }
    public int PeriodDays { get; set; }
}

public sealed class TopItem
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public sealed class DailyActivity
{
    public DateTime Date { get; set; }
    public int EventCount { get; set; }
    public int CommandCount { get; set; }
    public int ToolCallCount { get; set; }
}
