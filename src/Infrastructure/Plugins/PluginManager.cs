using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Plugins;

public sealed class PluginManager : IPluginManager
{
    private readonly PluginLoader _loader;
    private readonly PluginRegistry _registry;
    private readonly IEventBus _eventBus;
    private readonly IToolRegistry _toolRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PluginManager> _logger;
    private readonly string _pluginsDirectory;
    private readonly PluginPermissionManager? _permissionManager;
    private readonly Dictionary<string, PluginContext> _activeContexts = new(StringComparer.OrdinalIgnoreCase);

    public PluginManager(
        PluginLoader loader,
        PluginRegistry registry,
        IEventBus eventBus,
        IToolRegistry toolRegistry,
        IServiceProvider serviceProvider,
        ILogger<PluginManager> logger,
        string pluginsDirectory = "Plugins",
        PluginPermissionManager? permissionManager = null)
    {
        _loader = loader;
        _registry = registry;
        _eventBus = eventBus;
        _toolRegistry = toolRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _pluginsDirectory = pluginsDirectory;
        _permissionManager = permissionManager;
    }

    public IReadOnlyList<PluginMetadata> GetAllMetadata()
    {
        return _registry.GetAll().Select(e => e.Metadata).ToList().AsReadOnly();
    }

    public PluginMetadata? GetMetadata(string pluginId)
    {
        return _registry.Get(pluginId)?.Metadata;
    }

    public async Task DiscoverAsync(string pluginsDirectory, CancellationToken cancellationToken = default)
    {
        var dir = string.IsNullOrEmpty(pluginsDirectory) ? _pluginsDirectory : pluginsDirectory;
        dir = Path.GetFullPath(dir);
        _logger.LogInformation("[PluginManager] Discovering plugins in: {Dir}", dir);

        var metadatas = _loader.DiscoverPlugins(dir);

        foreach (var meta in metadatas)
        {
            _logger.LogInformation("[PluginManager] Discovered: {Name} v{Version} ({Id})",
                meta.Name, meta.Version, meta.Id);

            if (_registry.Get(meta.Id) is null)
            {
                try
                {
                    await LoadAsync(meta.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[PluginManager] Failed to load discovered plugin: {Id}", meta.Id);
                }
            }
        }

        _logger.LogInformation("[PluginManager] Discovery complete. Found {Count} plugins.", metadatas.Count);
    }

    public Task LoadAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var existing = _registry.Get(pluginId);
        if (existing is not null)
        {
            _logger.LogWarning("[PluginManager] Plugin already loaded: {PluginId}", pluginId);
            return Task.CompletedTask;
        }

        var metadata = FindMetadata(pluginId);
        if (metadata is null)
            throw new InvalidOperationException($"Plugin not discovered: {pluginId}");

        _logger.LogInformation("[PluginManager] Loading plugin: {Name}", metadata.Name);

        try
        {
            var assembly = _loader.LoadPlugin(pluginId, metadata.AssemblyPath);
            var pluginType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            if (pluginType is null)
                throw new InvalidOperationException($"No IPlugin implementation found in {metadata.AssemblyPath}");

            var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;

            _registry.Register(metadata, plugin);
            _registry.UpdateState(pluginId, PluginState.Loaded);

            _logger.LogInformation("[PluginManager] Loaded: {Name}", metadata.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PluginManager] Failed to load plugin: {PluginId}", pluginId);
            _registry.UpdateState(pluginId, PluginState.Error);
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task UnloadAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var entry = _registry.Get(pluginId);
        if (entry is null)
        {
            _logger.LogWarning("[PluginManager] Plugin not found for unload: {PluginId}", pluginId);
            return;
        }

        _logger.LogInformation("[PluginManager] Unloading: {Name}", entry.Metadata.Name);

        try
        {
            if (entry.State == PluginState.Running)
                await StopAsync(pluginId, cancellationToken);

            if (entry.State == PluginState.Initialized || entry.State == PluginState.Stopped)
            {
                await entry.Plugin.StopAsync(cancellationToken);
            }

            entry.Plugin.Dispose();
            _loader.UnloadPlugin(pluginId);
            _registry.Remove(pluginId);
            _registry.UpdateState(pluginId, PluginState.Stopped);
            lock (_activeContexts) _activeContexts.Remove(pluginId);

            _logger.LogInformation("[PluginManager] Unloaded: {Name}", entry.Metadata.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PluginManager] Failed to unload plugin: {PluginId}", pluginId);
            throw;
        }
    }

    public async Task StartAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var entry = _registry.Get(pluginId);
        if (entry is null)
            throw new InvalidOperationException($"Plugin not loaded: {pluginId}");

        if (entry.State == PluginState.Running)
        {
            _logger.LogWarning("[PluginManager] Plugin already running: {PluginId}", pluginId);
            return;
        }

        _logger.LogInformation("[PluginManager] Starting: {Name}", entry.Metadata.Name);

        try
        {
            var context = new PluginContext(
                _eventBus,
                _toolRegistry,
                _serviceProvider,
                _pluginsDirectory,
                entry.Metadata,
                _permissionManager);
            lock (_activeContexts) _activeContexts[pluginId] = context;
            await entry.Plugin.InitializeAsync(context, cancellationToken);
            _registry.UpdateState(pluginId, PluginState.Initialized);

            await entry.Plugin.StartAsync(cancellationToken);
            _registry.UpdateState(pluginId, PluginState.Running);

            _logger.LogInformation("[PluginManager] Started: {Name} (Tools: {ToolCount})",
                entry.Metadata.Name, context.Tools.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PluginManager] Failed to start plugin: {PluginId}", pluginId);
            _registry.UpdateState(pluginId, PluginState.Error);
            throw;
        }
    }

    public async Task StopAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var entry = _registry.Get(pluginId);
        if (entry is null)
        {
            _logger.LogWarning("[PluginManager] Plugin not found: {PluginId}", pluginId);
            return;
        }

        if (entry.State != PluginState.Running)
            return;

        _logger.LogInformation("[PluginManager] Stopping: {Name}", entry.Metadata.Name);

        try
        {
            await entry.Plugin.StopAsync(cancellationToken);
            _registry.UpdateState(pluginId, PluginState.Stopped);
            lock (_activeContexts) _activeContexts.Remove(pluginId);
            _logger.LogInformation("[PluginManager] Stopped: {Name}", entry.Metadata.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PluginManager] Failed to stop plugin: {PluginId}", pluginId);
            _registry.UpdateState(pluginId, PluginState.Error);
            throw;
        }
    }

    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in _registry.GetAll())
        {
            if (entry.State == PluginState.Loaded)
            {
                await StartAsync(entry.Metadata.Id, cancellationToken);
            }
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in _registry.GetAll().Reverse())
        {
            if (entry.State == PluginState.Running)
            {
                await StopAsync(entry.Metadata.Id, cancellationToken);
            }
        }
    }

    public IPlugin? GetPlugin(string pluginId)
    {
        return _registry.Get(pluginId)?.Plugin;
    }

    /// <summary>
    /// Returns the names of the tools registered by a running plugin, or an
    /// empty collection when the plugin is not currently started.
    /// </summary>
    public IReadOnlyList<string> GetPluginTools(string pluginId)
    {
        PluginContext? context;
        lock (_activeContexts) _activeContexts.TryGetValue(pluginId, out context);
        return context is null
            ? Array.Empty<string>()
            : context.Tools.Select(t => t.Name).ToList();
    }

    private PluginMetadata? FindMetadata(string pluginId)
    {
        var all = _loader.DiscoverPlugins(_pluginsDirectory);
        return all.FirstOrDefault(m => m.Id == pluginId);
    }
}
