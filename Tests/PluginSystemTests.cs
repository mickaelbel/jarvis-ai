using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Plugins;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class PluginSystemTests
{
    private static (PluginManager manager, PluginLoader loader, PluginRegistry registry, ToolRegistry toolRegistry, InMemoryEventBus eventBus) CreateSystem(string? pluginsDir = null)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var toolRegistry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);

        var manager = new PluginManager(
            loader, registry, eventBus, toolRegistry,
            new ServiceProviderForTests(),
            NullLogger<PluginManager>.Instance,
            pluginsDir ?? Path.Combine(Path.GetTempPath(), "jarvis_test_plugins"));

        return (manager, loader, registry, toolRegistry, eventBus);
    }

    [Fact]
    public void PluginRegistry_register_and_retrieve()
    {
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);
        var plugin = new MockTestPlugin();
        var metadata = new PluginMetadata { Id = "test", Name = "Test", Version = "1.0" };

        registry.Register(metadata, plugin);

        var entry = registry.Get("test");
        Assert.NotNull(entry);
        Assert.Equal("Test", entry!.Metadata.Name);
        Assert.Equal(PluginState.Loaded, entry.State);
    }

    [Fact]
    public void PluginRegistry_update_state()
    {
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);
        var plugin = new MockTestPlugin();
        var metadata = new PluginMetadata { Id = "test", Name = "Test", Version = "1.0" };

        registry.Register(metadata, plugin);
        registry.UpdateState("test", PluginState.Running);

        var entry = registry.Get("test");
        Assert.Equal(PluginState.Running, entry!.State);
    }

    [Fact]
    public void PluginRegistry_remove()
    {
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);
        var plugin = new MockTestPlugin();
        var metadata = new PluginMetadata { Id = "test", Name = "Test", Version = "1.0" };

        registry.Register(metadata, plugin);
        var removed = registry.Remove("test");

        Assert.True(removed);
        Assert.Null(registry.Get("test"));
    }

    [Fact]
    public void PluginRegistry_get_all()
    {
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);
        registry.Register(new PluginMetadata { Id = "a", Name = "A", Version = "1.0" }, new MockTestPlugin());
        registry.Register(new PluginMetadata { Id = "b", Name = "B", Version = "2.0" }, new MockTestPlugin());

        var all = registry.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task PluginManager_lifecycle_start_stop()
    {
        var (manager, _, registry, _, _) = CreateSystem();
        var plugin = new MockTestPlugin();
        var metadata = new PluginMetadata { Id = "mock", Name = "Mock Plugin", Version = "1.0" };

        registry.Register(metadata, plugin);

        await manager.StartAsync("mock");
        Assert.Equal(PluginState.Running, plugin.State);

        await manager.StopAsync("mock");
        Assert.Equal(PluginState.Stopped, plugin.State);
    }

    [Fact]
    public async Task PluginManager_start_all()
    {
        var (manager, _, registry, _, _) = CreateSystem();
        var plugin1 = new MockTestPlugin();
        var plugin2 = new MockTestPlugin();
        registry.Register(new PluginMetadata { Id = "p1", Name = "P1", Version = "1.0" }, plugin1);
        registry.Register(new PluginMetadata { Id = "p2", Name = "P2", Version = "1.0" }, plugin2);

        await manager.StartAllAsync();

        Assert.Equal(PluginState.Running, plugin1.State);
        Assert.Equal(PluginState.Running, plugin2.State);
    }

    [Fact]
    public async Task PluginManager_start_plugin_registers_tools()
    {
        var (manager, _, registry, toolRegistry, _) = CreateSystem();
        var plugin = new PluginWithTool();
        registry.Register(new PluginMetadata { Id = "withTool", Name = "WithTool", Version = "1.0" }, plugin);

        await manager.StartAsync("withTool");

        var tools = toolRegistry.GetAll();
        Assert.Single(tools);
        Assert.Equal("mock_tool", tools[0].Name);
    }

    [Fact]
    public async Task PluginManager_start_nonexistent_throws()
    {
        var (manager, _, _, _, _) = CreateSystem();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync("nonexistent"));
    }

    [Fact]
    public async Task PluginManager_start_already_running_is_noop()
    {
        var (manager, _, registry, _, _) = CreateSystem();
        var plugin = new MockTestPlugin();
        registry.Register(new PluginMetadata { Id = "test", Name = "Test", Version = "1.0" }, plugin);

        await manager.StartAsync("test");
        await manager.StartAsync("test");

        Assert.Equal(PluginState.Running, plugin.State);
    }

    [Fact]
    public async Task Plugin_with_error_sets_error_state()
    {
        var (manager, _, registry, _, _) = CreateSystem();
        var plugin = new ErrorPlugin();
        registry.Register(new PluginMetadata { Id = "error", Name = "Error", Version = "1.0" }, plugin);

        await Assert.ThrowsAsync<ApplicationException>(() => manager.StartAsync("error"));
        Assert.Equal(PluginState.Error, registry.Get("error")!.State);
    }

    [Fact]
    public void PluginLoader_discover_returns_empty_for_missing_dir()
    {
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        var result = loader.DiscoverPlugins("/nonexistent/path");
        Assert.Empty(result);
    }

    [Fact]
    public async Task PluginManager_discover_loads_and_start_all_starts_real_plugin()
    {
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
        var (manager, _, registry, toolRegistry, _) = CreateSystem(pluginsDir);

        await manager.DiscoverAsync(pluginsDir);

        var metadata = manager.GetMetadata("JarvisAI.Plugins.SystemControl");
        Assert.NotNull(metadata);
        Assert.Equal(PluginState.Loaded, registry.Get("JarvisAI.Plugins.SystemControl")!.State);

        await manager.StartAllAsync();

        Assert.Equal(PluginState.Running, registry.Get("JarvisAI.Plugins.SystemControl")!.State);
        Assert.Contains(toolRegistry.GetAll(), t => t.Name == "sys_system_info");
        Assert.Contains(toolRegistry.GetAll(), t => t.Name == "sys_process_list");
    }

    [Fact]
    public void Plugin_dispose_cleans_up()
    {
        var plugin = new MockTestPlugin();
        var metadata = new PluginMetadata { Id = "test", Name = "Test", Version = "1.0" };

        var context = new PluginContext(
            new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance),
            new ToolRegistry(NullLogger<ToolRegistry>.Instance),
            new ServiceProviderForTests(),
            Path.GetTempPath());

        var tool = new DummyTool();
        context.RegisterTool(tool);

        Assert.Single(context.Tools);

        context.UnregisterTools();
        Assert.Empty(context.Tools);
    }
}

internal sealed class MockTestPlugin : IPlugin
{
    public PluginMetadata Metadata { get; } = new() { Id = "mock", Name = "Mock", Version = "1.0" };
    public PluginState State { get; set; } = PluginState.Discovered;

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken = default)
    {
        State = PluginState.Initialized;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        State = PluginState.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        State = PluginState.Stopped;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

internal sealed class PluginWithTool : IPlugin
{
    public PluginMetadata Metadata { get; } = new() { Id = "withTool", Name = "WithTool", Version = "1.0" };
    public PluginState State { get; set; } = PluginState.Discovered;

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken = default)
    {
        context.RegisterTool(new DummyTool());
        State = PluginState.Initialized;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        State = PluginState.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        State = PluginState.Stopped;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

internal sealed class ErrorPlugin : IPlugin
{
    public PluginMetadata Metadata { get; } = new() { Id = "error", Name = "Error", Version = "1.0" };
    public PluginState State { get; set; } = PluginState.Discovered;

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken = default)
        => throw new ApplicationException("Plugin initialization failed");

    public Task StartAsync(CancellationToken cancellationToken = default)
        => throw new ApplicationException("Plugin start failed");

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Dispose() { }
}

internal sealed class DummyTool : ITool
{
    public string Name => "mock_tool";
    public string Description => "A mock tool for testing";
    public string Category => "test";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        => Task.FromResult(ToolResult.Succeeded("mock result"));
}

internal sealed class ServiceProviderForTests : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
