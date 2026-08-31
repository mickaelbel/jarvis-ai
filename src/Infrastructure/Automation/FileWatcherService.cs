using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface IFileWatcherService
{
    Task<WatchResult> WatchFolderAsync(string path, Action<FileChangeEvent> callback, CancellationToken ct = default);
    Task StopWatchingAsync(string watchId);
    List<WatchInfo> GetActiveWatches();
}

public sealed class FileWatcherService : IFileWatcherService
{
    private readonly ILogger<FileWatcherService> _logger;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, List<FileChangeEvent>> _events = new();

    public FileWatcherService(ILogger<FileWatcherService> logger)
    {
        _logger = logger;
    }

    public Task<WatchResult> WatchFolderAsync(string path, Action<FileChangeEvent> callback, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return Task.FromResult(new WatchResult { Success = false, ErrorMessage = $"Folder not found: {path}" });
        }

        var watchId = Guid.NewGuid().ToString("N")[..8];

        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
        };

        watcher.Created += (s, e) => HandleEvent(watchId, e, FileChangeType.Created, callback);
        watcher.Changed += (s, e) => HandleEvent(watchId, e, FileChangeType.Modified, callback);
        watcher.Deleted += (s, e) => HandleEvent(watchId, e, FileChangeType.Deleted, callback);
        watcher.Renamed += (s, e) => HandleEvent(watchId, e, FileChangeType.Renamed, callback, e.OldFullPath);

        _watchers[watchId] = watcher;
        _events[watchId] = new List<FileChangeEvent>();

        _logger.LogInformation("[FileWatch] Started watching: {Path} (ID: {Id})", path, watchId);

        return Task.FromResult(new WatchResult
        {
            Success = true,
            WatchId = watchId,
            Path = path
        });
    }

    public Task StopWatchingAsync(string watchId)
    {
        if (_watchers.TryGetValue(watchId, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watchers.Remove(watchId);
            _events.Remove(watchId);
            _logger.LogInformation("[FileWatch] Stopped: {Id}", watchId);
        }
        return Task.CompletedTask;
    }

    public List<WatchInfo> GetActiveWatches()
    {
        return _watchers.Select(kvp => new WatchInfo
        {
            Id = kvp.Key,
            Path = kvp.Value.Path,
            EventCount = _events.GetValueOrDefault(kvp.Key)?.Count ?? 0
        }).ToList();
    }

    private void HandleEvent(string watchId, FileSystemEventArgs e, FileChangeType type, Action<FileChangeEvent> callback, string? oldPath = null)
    {
        var change = new FileChangeEvent
        {
            WatchId = watchId,
            Type = type,
            Path = e.FullPath,
            OldPath = oldPath,
            Timestamp = DateTime.UtcNow
        };

        if (_events.ContainsKey(watchId))
            _events[watchId].Add(change);

        callback(change);
        _logger.LogDebug("[FileWatch] {Type}: {Path}", type, e.Name);
    }
}

public sealed class WatchResult
{
    public bool Success { get; set; }
    public string WatchId { get; set; } = "";
    public string Path { get; set; } = "";
    public string? ErrorMessage { get; set; }
}

public sealed class WatchInfo
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public int EventCount { get; set; }
}

public sealed class FileChangeEvent
{
    public string WatchId { get; set; } = "";
    public FileChangeType Type { get; set; }
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum FileChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed
}
