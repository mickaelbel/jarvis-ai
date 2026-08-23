namespace JarvisAI.Application.Plugins;

public interface IPlugin : IDisposable
{
    PluginMetadata Metadata { get; }
    PluginState State { get; }
    Task InitializeAsync(PluginContext context, CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
