using JarvisAI.Application.Plugins;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace JarvisAI.Infrastructure.Plugins;

public sealed class PluginRegistry
{
    private readonly ConcurrentDictionary<string, PluginEntry> _plugins = new();
    private readonly ILogger<PluginRegistry> _logger;

    public PluginRegistry(ILogger<PluginRegistry> logger)
    {
        _logger = logger;
    }

    public void Register(PluginMetadata metadata, IPlugin plugin)
    {
        metadata.State = PluginState.Loaded;
        var entry = new PluginEntry { Metadata = metadata, Plugin = plugin, State = PluginState.Loaded };
        _plugins[metadata.Id] = entry;
        _logger.LogInformation("[PluginRegistry] Registered: {Name} v{Version}", metadata.Name, metadata.Version);
    }

    public void UpdateState(string pluginId, PluginState state)
    {
        if (_plugins.TryGetValue(pluginId, out var entry))
        {
            entry.State = state;
            entry.Metadata.State = state;
            _logger.LogDebug("[PluginRegistry] {PluginId} → {State}", pluginId, state);
        }
    }

    public PluginEntry? Get(string pluginId)
    {
        _plugins.TryGetValue(pluginId, out var entry);
        return entry;
    }

    public IReadOnlyList<PluginEntry> GetAll()
    {
        return _plugins.Values.ToList().AsReadOnly();
    }

    public bool Remove(string pluginId)
    {
        return _plugins.TryRemove(pluginId, out _);
    }
}

public sealed class PluginEntry
{
    public required PluginMetadata Metadata { get; init; }
    public required IPlugin Plugin { get; init; }
    public PluginState State { get; set; }
}
