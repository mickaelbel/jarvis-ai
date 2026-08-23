using System.Collections.Concurrent;

namespace JarvisAI.Application.Tools;

public sealed class ToolUsageStats
{
    public required string ToolName { get; init; }
    public long Calls { get; set; }
    public long Failures { get; set; }
    public double TotalDurationMs { get; set; }
    public double AverageDurationMs => Calls > 0 ? TotalDurationMs / Calls : 0;
    public double SuccessRate => Calls > 0 ? 1.0 - (double)Failures / Calls : 0;
    public DateTime? LastUsed { get; set; }
}

public interface IToolUsageTracker
{
    void Record(string toolName, long durationMs, bool success);
    IReadOnlyList<ToolUsageStats> GetAll();
    ToolUsageStats? Get(string toolName);
    void Reset(string toolName);
}

public sealed class InMemoryToolUsageTracker : IToolUsageTracker
{
    private readonly ConcurrentDictionary<string, ToolUsageStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string toolName, long durationMs, bool success)
    {
        var stat = _stats.GetOrAdd(toolName, name => new ToolUsageStats { ToolName = name });
        lock (stat)
        {
            stat.Calls++;
            if (!success) stat.Failures++;
            stat.TotalDurationMs += durationMs;
            stat.LastUsed = DateTime.UtcNow;
        }
    }

    public IReadOnlyList<ToolUsageStats> GetAll()
        => _stats.Values.OrderByDescending(s => s.Calls).ToArray();

    public ToolUsageStats? Get(string toolName)
        => _stats.TryGetValue(toolName, out var stat) ? stat : null;

    public void Reset(string toolName)
    {
        if (_stats.TryRemove(toolName, out var stat))
        {
            lock (stat)
            {
                stat.Calls = 0;
                stat.Failures = 0;
                stat.TotalDurationMs = 0;
                stat.LastUsed = null;
            }
            _stats.TryAdd(toolName, stat);
        }
    }
}
