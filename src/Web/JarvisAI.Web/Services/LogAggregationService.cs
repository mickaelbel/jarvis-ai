using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ILogAggregationService
{
    IReadOnlyList<AggregatedLog> GetLogs(string? source = null, LogLevel? level = null, DateTime? from = null, DateTime? to = null);
    void AddLog(string source, LogLevel level, string message, Dictionary<string, string>? properties = null);
    LogStatistics GetStatistics(int hours = 24);
    IReadOnlyList<LogSource> GetSources();
    void ClearLogs(DateTime? olderThan = null);
}

public sealed class LogAggregationService : ILogAggregationService
{
    private readonly ILogger<LogAggregationService> _logger;
    private readonly string _storagePath;
    private readonly List<AggregatedLog> _logs = new();

    public LogAggregationService(ILogger<LogAggregationService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "aggregated_logs.json");
        Load();
    }

    public IReadOnlyList<AggregatedLog> GetLogs(string? source = null, LogLevel? level = null, DateTime? from = null, DateTime? to = null)
    {
        var query = _logs.AsEnumerable();

        if (source is not null)
            query = query.Where(l => l.Source.Equals(source, StringComparison.OrdinalIgnoreCase));

        if (level.HasValue)
            query = query.Where(l => l.Level >= level.Value);

        if (from.HasValue)
            query = query.Where(l => l.Timestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(l => l.Timestamp <= to.Value);

        return query.OrderByDescending(l => l.Timestamp).Take(1000).ToList();
    }

    public void AddLog(string source, LogLevel level, string message, Dictionary<string, string>? properties = null)
    {
        var log = new AggregatedLog
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Source = source,
            Level = level,
            Message = message,
            Properties = properties ?? new(),
            Timestamp = DateTime.UtcNow
        };

        _logs.Add(log);

        // Keep only last 10000 logs
        if (_logs.Count > 10000)
            _logs.RemoveRange(0, _logs.Count - 10000);

        Save();
    }

    public LogStatistics GetStatistics(int hours = 24)
    {
        var cutoff = DateTime.UtcNow.AddHours(-hours);
        var recent = _logs.Where(l => l.Timestamp >= cutoff).ToList();

        return new LogStatistics
        {
            TotalLogs = recent.Count,
            ErrorCount = recent.Count(l => l.Level == LogLevel.Error),
            WarningCount = recent.Count(l => l.Level == LogLevel.Warning),
            InfoCount = recent.Count(l => l.Level == LogLevel.Information),
            DebugCount = recent.Count(l => l.Level == LogLevel.Debug),
            Sources = recent.GroupBy(l => l.Source).ToDictionary(g => g.Key, g => g.Count()),
            TopErrors = recent.Where(l => l.Level == LogLevel.Error)
                             .GroupBy(l => l.Message)
                             .OrderByDescending(g => g.Count())
                             .Take(5)
                             .Select(g => new TopError { Message = g.Key, Count = g.Count() })
                             .ToList(),
            PeriodHours = hours
        };
    }

    public IReadOnlyList<LogSource> GetSources()
    {
        return _logs.GroupBy(l => l.Source)
                   .Select(g => new LogSource
                   {
                       Name = g.Key,
                       LogCount = g.Count(),
                       LastLog = g.Max(l => l.Timestamp),
                       ErrorCount = g.Count(l => l.Level == LogLevel.Error)
                   })
                   .OrderByDescending(s => s.LogCount)
                   .ToList();
    }

    public void ClearLogs(DateTime? olderThan = null)
    {
        if (olderThan.HasValue)
            _logs.RemoveAll(l => l.Timestamp < olderThan.Value);
        else
            _logs.Clear();

        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<AggregatedLog>>(json);
                if (loaded is not null) _logs.AddRange(loaded);
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

            // Keep only last 5000 on save
            if (_logs.Count > 5000)
                _logs.RemoveRange(0, _logs.Count - 5000);

            var json = JsonSerializer.Serialize(_logs, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class AggregatedLog
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public LogLevel Level { get; set; }
    public string Message { get; set; } = "";
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTime Timestamp { get; set; }
}

public sealed class LogStatistics
{
    public int TotalLogs { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public int DebugCount { get; set; }
    public Dictionary<string, int> Sources { get; set; } = new();
    public List<TopError> TopErrors { get; set; } = new();
    public int PeriodHours { get; set; }
}

public sealed class TopError
{
    public string Message { get; set; } = "";
    public int Count { get; set; }
}

public sealed class LogSource
{
    public string Name { get; set; } = "";
    public int LogCount { get; set; }
    public DateTime LastLog { get; set; }
    public int ErrorCount { get; set; }
}
