using System.Collections.Generic;

namespace JarvisAI.Application.Debug;

public sealed record DebugToolCall
{
    public DateTimeOffset Timestamp { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();
    public string Output { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }

    public string Command =>
        Arguments.TryGetValue("command", out var c) ? c : string.Empty;

    public string FilePath =>
        Arguments.TryGetValue("path", out var p) ? p : string.Empty;

    public string FileAction =>
        Arguments.TryGetValue("action", out var a) ? a : string.Empty;

    public string Content =>
        Arguments.TryGetValue("content", out var c) ? c : string.Empty;

    public string OldString =>
        Arguments.TryGetValue("old_string", out var o) ? o : string.Empty;

    public string NewString =>
        Arguments.TryGetValue("new_string", out var n) ? n : string.Empty;
}

public interface IDebugToolFeed
{
    event EventHandler? Changed;
    IReadOnlyList<DebugToolCall> Recent(int count = 100);
    void Record(DebugToolCall call);
}

public sealed class InMemoryDebugToolFeed : IDebugToolFeed
{
    private const int MaxEntries = 200;
    private readonly List<DebugToolCall> _entries = new();
    private readonly object _lock = new();

    public event EventHandler? Changed;

    public IReadOnlyList<DebugToolCall> Recent(int count = 100)
    {
        lock (_lock)
        {
            var take = Math.Min(count, _entries.Count);
            return _entries.GetRange(_entries.Count - take, take).AsReadOnly();
        }
    }

    public void Record(DebugToolCall call)
    {
        lock (_lock)
        {
            _entries.Add(call);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
