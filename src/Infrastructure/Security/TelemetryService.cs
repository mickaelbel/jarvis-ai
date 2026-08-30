using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Security;

public interface ITelemetryService
{
    void TrackEvent(string eventName, Dictionary<string, string>? properties = null);
    void TrackError(Exception ex, Dictionary<string, string>? properties = null);
    void TrackPerformance(string metric, double value, Dictionary<string, string>? properties = null);
    Task FlushAsync(CancellationToken ct = default);
    TelemetrySummary GetSummary();
}

public sealed class TelemetryService : ITelemetryService
{
    private readonly ILogger<TelemetryService> _logger;
    private readonly string _storagePath;
    private readonly List<TelemetryEvent> _events = new();
    private readonly bool _enabled;
    private const int MaxEvents = 5000;

    public TelemetryService(ILogger<TelemetryService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "telemetry.json");
        _enabled = true;
        Load();
    }

    public void TrackEvent(string eventName, Dictionary<string, string>? properties = null)
    {
        if (!_enabled) return;

        lock (_events)
        {
            _events.Add(new TelemetryEvent
            {
                Type = TelemetryEventType.Event,
                Name = eventName,
                Properties = properties ?? new(),
                Timestamp = DateTime.UtcNow
            });

            if (_events.Count > MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents);
        }
    }

    public void TrackError(Exception ex, Dictionary<string, string>? properties = null)
    {
        if (!_enabled) return;

        lock (_events)
        {
            _events.Add(new TelemetryEvent
            {
                Type = TelemetryEventType.Error,
                Name = ex.GetType().Name,
                Message = ex.Message,
                StackTrace = ex.StackTrace,
                Properties = properties ?? new(),
                Timestamp = DateTime.UtcNow
            });
        }

        _logger.LogWarning(ex, "[Telemetry] Error tracked: {Type}", ex.GetType().Name);
    }

    public void TrackPerformance(string metric, double value, Dictionary<string, string>? properties = null)
    {
        if (!_enabled) return;

        lock (_events)
        {
            _events.Add(new TelemetryEvent
            {
                Type = TelemetryEventType.Performance,
                Name = metric,
                Value = value,
                Properties = properties ?? new(),
                Timestamp = DateTime.UtcNow
            });
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            List<TelemetryEvent> snapshot;
            lock (_events)
            {
                snapshot = _events.ToList();
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_storagePath, json, ct);

            _logger.LogDebug("[Telemetry] Flushed {Count} events", snapshot.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Telemetry] Flush failed");
        }
    }

    public TelemetrySummary GetSummary()
    {
        lock (_events)
        {
            var errors = _events.Count(e => e.Type == TelemetryEventType.Error);
            var perf = _events.Where(e => e.Type == TelemetryEventType.Performance).ToList();

            return new TelemetrySummary
            {
                TotalEvents = _events.Count,
                TotalErrors = errors,
                AverageResponseTime = perf.Any() ? perf.Average(e => e.Value ?? 0) : 0,
                EventsByType = _events.GroupBy(e => e.Type).ToDictionary(g => g.Key.ToString(), g => g.Count())
            };
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<TelemetryEvent>>(json);
                if (loaded is not null)
                {
                    _events.AddRange(loaded.TakeLast(1000));
                }
            }
        }
        catch { }
    }
}

public sealed class TelemetryEvent
{
    public TelemetryEventType Type { get; set; }
    public string Name { get; set; } = "";
    public string? Message { get; set; }
    public string? StackTrace { get; set; }
    public double? Value { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTime Timestamp { get; set; }
}

public enum TelemetryEventType { Event, Error, Performance }

public sealed class TelemetrySummary
{
    public int TotalEvents { get; set; }
    public int TotalErrors { get; set; }
    public double AverageResponseTime { get; set; }
    public Dictionary<string, int> EventsByType { get; set; } = new();
}
