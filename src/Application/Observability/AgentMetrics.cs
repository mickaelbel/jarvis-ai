using System.Collections.Concurrent;
using System.Diagnostics;

namespace JarvisAI.Application.Observability;

/// <summary>
/// Suivi léger des métriques agentiques en temps réel.
/// Thread-safe, sans allocation excessive.
/// </summary>
public sealed class AgentMetrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentQueue<(string Name, double Ms, DateTime Time)> _recentLatencies = new();
    private const int MaxRecentLatencies = 200;

    public static AgentMetrics Instance { get; } = new();

    public void Increment(string counter) =>
        _counters.AddOrUpdate(counter, 1, (_, v) => v + 1);

    public void SetGauge(string gauge, double value) =>
        _gauges[gauge] = value;

    public void RecordLatency(string name, double milliseconds)
    {
        _recentLatencies.Enqueue((name, milliseconds, DateTime.UtcNow));
        while (_recentLatencies.Count > MaxRecentLatencies)
            _recentLatencies.TryDequeue(out _);
    }

    public double StartTimer()
    {
        return Stopwatch.GetTimestamp();
    }

    public double StopTimer(double start)
    {
        return (Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency * 1000.0;
    }

    public Dictionary<string, long> GetAllCounters() => new(_counters);
    public Dictionary<string, double> GetAllGauges() => new(_gauges);

    public Dictionary<string, double> GetAvgLatencies()
    {
        var groups = _recentLatencies.ToArray()
            .GroupBy(x => x.Name)
            .ToDictionary(g => g.Key, g => g.Average(x => x.Ms));
        return groups;
    }

    public string GetSummary()
    {
        var counters = GetAllCounters();
        var gauges = GetAllGauges();
        var latencies = GetAvgLatencies();

        var parts = new List<string>();
        foreach (var kv in counters)
            parts.Add($"{kv.Key}: {kv.Value}");
        foreach (var kv in latencies)
            parts.Add($"{kv.Key}_avg: {kv.Value:F0}ms");

        return string.Join(" | ", parts);
    }
}
