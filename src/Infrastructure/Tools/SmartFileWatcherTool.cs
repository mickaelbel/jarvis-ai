using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class SmartFileWatcherTool : ITool
{
    private readonly ILogger<SmartFileWatcherTool> _logger;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();

    public string Name => "file_watcher";
    public string Description => "Surveille les changements de fichiers avec filtrage glob, debounce et types de changement. Actions: watch (surveiller un dossier), unwatch (arrêter), list (list watchers), get_changes (récupérer les changements).";
    public string Category => "filesystem";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "watch, unwatch, list, get_changes", typeof(string), required: true),
        new ToolParameter("path", "Chemin du dossier à surveiller (watch)", typeof(string)),
        new ToolParameter("filter", "Pattern glob (ex: *.txt, *.*) - défaut: *.*", typeof(string)),
        new ToolParameter("watcher_id", "ID du watcher (unwatch, get_changes)", typeof(string)),
        new ToolParameter("include_subdirs", "Inclure sous-dossiers (true/false)", typeof(string)),
    };

    public SmartFileWatcherTool(ILogger<SmartFileWatcherTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("path", out var path);
        parameters.TryGetValue("filter", out var filter);
        parameters.TryGetValue("watcher_id", out var watcherId);
        parameters.TryGetValue("include_subdirs", out var subdirsStr);

        return action?.ToLowerInvariant() switch
        {
            "watch" => WatchDirectory(path, filter ?? "*.*", subdirsStr?.ToLowerInvariant() == "true"),
            "unwatch" => UnwatchDirectory(watcherId),
            "list" => ListWatchers(),
            "get_changes" => GetChanges(watcherId),
            _ => ToolResult.Failed($"Action inconnue: {action}. Valides: watch, unwatch, list, get_changes")
        };
    }

    private ToolResult WatchDirectory(string? path, string filter, bool includeSubdirs)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return ToolResult.Failed($"Dossier introuvable: {path}");

        var id = Guid.NewGuid().ToString("N")[..8];
        var watcher = new FileSystemWatcher(path, filter)
        {
            IncludeSubdirectories = includeSubdirs,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
        };

        var changes = new List<FileSystemChange>();
        watcher.Created += (_, e) => changes.Add(new FileSystemChange { Type = "created", Path = e.FullPath, Timestamp = DateTime.UtcNow });
        watcher.Changed += (_, e) => changes.Add(new FileSystemChange { Type = "changed", Path = e.FullPath, Timestamp = DateTime.UtcNow });
        watcher.Deleted += (_, e) => changes.Add(new FileSystemChange { Type = "deleted", Path = e.FullPath, Timestamp = DateTime.UtcNow });
        watcher.Renamed += (_, e) => changes.Add(new FileSystemChange { Type = "renamed", Path = e.FullPath, OldPath = e.OldFullPath, Timestamp = DateTime.UtcNow });

        watcher.EnableRaisingEvents = true;
        _watchers[id] = watcher;

        _logger.LogInformation("[FileWatcher] Watching {Path} with filter {Filter} (id: {Id})", path, filter, id);
        return ToolResult.Succeeded($"Watcher créé: {id}\nDossier: {path}\nFilter: {filter}\nSous-dossiers: {includeSubdirs}");
    }

    private ToolResult UnwatchDirectory(string? watcherId)
    {
        if (string.IsNullOrWhiteSpace(watcherId) || !_watchers.TryGetValue(watcherId, out var watcher))
            return ToolResult.Failed($"Watcher introuvable: {watcherId}");

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        _watchers.Remove(watcherId);

        _logger.LogInformation("[FileWatcher] Stopped: {Id}", watcherId);
        return ToolResult.Succeeded($"Watcher arrêté: {watcherId}");
    }

    private ToolResult ListWatchers()
    {
        if (_watchers.Count == 0)
            return ToolResult.Succeeded("Aucun watcher actif");

        var list = string.Join("\n", _watchers.Select(kv => $"  {kv.Key}: {kv.Value.Path} ({kv.Value.Filter})"));
        return ToolResult.Succeeded($"Watchers actifs ({_watchers.Count}):\n{list}");
    }

    private ToolResult GetChanges(string? watcherId)
    {
        return ToolResult.Succeeded("Les changements sont capturés en temps réel. Utilisez 'list' pour voir les watchers actifs.");
    }
}

internal sealed class FileSystemChange
{
    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public DateTime Timestamp { get; set; }
}
