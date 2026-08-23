namespace JarvisAI.Application.Plugins;

public interface IPluginManager
{
    IReadOnlyList<PluginMetadata> GetAllMetadata();
    PluginMetadata? GetMetadata(string pluginId);
    Task DiscoverAsync(string pluginsDirectory, CancellationToken cancellationToken = default);
    Task LoadAsync(string pluginId, CancellationToken cancellationToken = default);
    Task UnloadAsync(string pluginId, CancellationToken cancellationToken = default);
    Task StartAsync(string pluginId, CancellationToken cancellationToken = default);
    Task StopAsync(string pluginId, CancellationToken cancellationToken = default);
    Task StartAllAsync(CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
    IPlugin? GetPlugin(string pluginId);
    IReadOnlyList<string> GetPluginTools(string pluginId);
}
