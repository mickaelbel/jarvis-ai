using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Security;

public interface IClipboardHistoryManager
{
    Task AddEntryAsync(string content, ClipboardEntryType type = ClipboardEntryType.Text, CancellationToken ct = default);
    Task<IReadOnlyList<ClipboardEntry>> SearchAsync(string? query = null, int maxCount = 50, CancellationToken ct = default);
    Task<ClipboardEntry?> GetEntryAsync(string entryId, CancellationToken ct = default);
    Task PinEntryAsync(string entryId, CancellationToken ct = default);
    Task DeleteEntryAsync(string entryId, CancellationToken ct = default);
    Task ClearAsync(bool keepPinned = true, CancellationToken ct = default);
}

public sealed class ClipboardHistoryManager : IClipboardHistoryManager
{
    private readonly ILogger<ClipboardHistoryManager> _logger;
    private readonly string _storagePath;
    private readonly List<ClipboardEntry> _entries = new();
    private const int MaxEntries = 200;

    public ClipboardHistoryManager(ILogger<ClipboardHistoryManager> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "clipboard_history.json");
        Load();
    }

    public async Task AddEntryAsync(string content, ClipboardEntryType type = ClipboardEntryType.Text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        lock (_entries)
        {
            var existing = _entries.FirstOrDefault(e => e.Content == content);
            if (existing is not null)
            {
                existing.UsageCount++;
                existing.LastUsed = DateTime.UtcNow;
            }
            else
            {
                _entries.Insert(0, new ClipboardEntry
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Content = content,
                    Type = type,
                    Preview = content.Length > 200 ? content[..200] + "..." : content,
                    CreatedAt = DateTime.UtcNow,
                    LastUsed = DateTime.UtcNow
                });
            }

            while (_entries.Count > MaxEntries)
            {
                var unpinned = _entries.FirstOrDefault(e => !e.IsPinned);
                if (unpinned is not null) _entries.Remove(unpinned);
                else break;
            }
        }

        Save();
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ClipboardEntry>> SearchAsync(string? query = null, int maxCount = 50, CancellationToken ct = default)
    {
        lock (_entries)
        {
            IEnumerable<ClipboardEntry> results = _entries;

            if (!string.IsNullOrWhiteSpace(query))
            {
                var lower = query.ToLowerInvariant();
                results = results.Where(e =>
                    e.Content.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                    e.Type.ToString().Contains(lower, StringComparison.OrdinalIgnoreCase));
            }

            return results
                .OrderByDescending(e => e.IsPinned)
                .ThenByDescending(e => e.LastUsed)
                .Take(maxCount)
                .ToList();
        }
    }

    public async Task<ClipboardEntry?> GetEntryAsync(string entryId, CancellationToken ct = default)
    {
        lock (_entries)
        {
            return _entries.FirstOrDefault(e => e.Id == entryId);
        }
    }

    public async Task PinEntryAsync(string entryId, CancellationToken ct = default)
    {
        lock (_entries)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == entryId);
            if (entry is not null)
            {
                entry.IsPinned = !entry.IsPinned;
                Save();
            }
        }
        await Task.CompletedTask;
    }

    public async Task DeleteEntryAsync(string entryId, CancellationToken ct = default)
    {
        lock (_entries)
        {
            _entries.RemoveAll(e => e.Id == entryId);
        }
        Save();
        await Task.CompletedTask;
    }

    public async Task ClearAsync(bool keepPinned = true, CancellationToken ct = default)
    {
        lock (_entries)
        {
            if (keepPinned)
                _entries.RemoveAll(e => !e.IsPinned);
            else
                _entries.Clear();
        }
        Save();
        await Task.CompletedTask;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(json);
                if (loaded is not null) _entries.AddRange(loaded);
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

            lock (_entries)
            {
                var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
        }
        catch { }
    }
}

public sealed class ClipboardEntry
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public string Preview { get; set; } = "";
    public ClipboardEntryType Type { get; set; }
    public bool IsPinned { get; set; }
    public int UsageCount { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsed { get; set; }
}

public enum ClipboardEntryType
{
    Text,
    Code,
    Image,
    Link,
    File
}
