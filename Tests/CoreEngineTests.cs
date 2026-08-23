using JarvisAI.Core.Engine;
using JarvisAI.Application.Plugins;
using JarvisAI.Infrastructure.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class CoreEngineTests
{
    [Fact]
    public async Task Engine_starts_and_stops_successfully()
    {
        var logger = NullLogger<CoreEngine>.Instance;
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var pluginManager = new MockPluginManager();
        var engine = new CoreEngine(logger, eventBus, pluginManager);

        Assert.Equal(EngineState.Stopped, engine.State);

        await engine.StartAsync();
        Assert.Equal(EngineState.Running, engine.State);

        await engine.StopAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
    }
}

internal sealed class MockPluginManager : IPluginManager
{
    public IReadOnlyList<PluginMetadata> GetAllMetadata() => Array.Empty<PluginMetadata>();
    public PluginMetadata? GetMetadata(string pluginId) => null;
    public Task DiscoverAsync(string pluginsDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task LoadAsync(string pluginId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UnloadAsync(string pluginId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StartAsync(string pluginId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(string pluginId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StartAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public IPlugin? GetPlugin(string pluginId) => null;
    public IReadOnlyList<string> GetPluginTools(string pluginId) => Array.Empty<string>();
}
