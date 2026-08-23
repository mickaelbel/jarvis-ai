using System.Collections.Concurrent;

namespace JarvisAI.Web.Services;

public sealed class ActionHistoryService
{
    private readonly ConcurrentQueue<HistoryEntry> _entries = new();
    private const int MaxEntries = 500;

    public event Action<HistoryEntry>? OnEntryAdded;

    public void AddEntry(HistoryEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
        OnEntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<HistoryEntry> GetEntries(string? type = null, string? search = null, int limit = 100)
    {
        var query = _entries.AsEnumerable();
        if (!string.IsNullOrEmpty(type))
            query = query.Where(e => e.Type == type);
        if (!string.IsNullOrEmpty(search))
            query = query.Where(e =>
                e.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(search, StringComparison.OrdinalIgnoreCase));
        return query.Reverse().Take(limit).ToList();
    }

    public void Clear() { while (_entries.TryDequeue(out _)) { } }
}

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "event";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "info";
    public string? Icon { get; set; }
    public string? User { get; set; }
    public Dictionary<string, string>? Details { get; set; }
}

public static class HistoryEntryTypes
{
    public const string Chat = "chat";
    public const string Tool = "tool";
    public const string Command = "command";
    public const string Plugin = "plugin";
    public const string System = "system";
    public const string Security = "security";
    public const string Memory = "memory";
    public const string Model = "model";
}

public static class HistoryEntryStatuses
{
    public const string Success = "success";
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
}
