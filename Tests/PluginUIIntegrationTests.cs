using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Plugins;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// Verifies the plugin information surfaced by the Plugins UI page: tool
/// enumeration for running plugins (via GetPluginTools), start/stop state
/// transitions and metadata listing.
/// </summary>
public class PluginUIIntegrationTests
{
    private static (PluginManager manager, PluginRegistry registry) CreateSystem()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var toolRegistry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);
        var manager = new PluginManager(
            new PluginLoader(NullLogger<PluginLoader>.Instance),
            registry,
            eventBus,
            toolRegistry,
            new ServiceProviderForTests(),
            NullLogger<PluginManager>.Instance,
            Path.Combine(Path.GetTempPath(), "jarvis_ui_test_plugins"));
        return (manager, registry);
    }

    private static PluginMetadata Register(PluginRegistry registry, string id, IPlugin plugin)
    {
        var metadata = new PluginMetadata { Id = id, Name = id, Version = "1.0.0" };
        registry.Register(metadata, plugin);
        return metadata;
    }

    [Fact]
    public void GetPluginTools_returns_empty_for_unknown_plugin()
    {
        var (manager, _) = CreateSystem();

        var tools = manager.GetPluginTools("does-not-exist");

        Assert.NotNull(tools);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task GetPluginTools_returns_tools_for_running_plugin()
    {
        var (manager, registry) = CreateSystem();
        Register(registry, "withTool", new PluginWithTool());

        await manager.StartAsync("withTool");

        var tools = manager.GetPluginTools("withTool");
        Assert.Contains("mock_tool", tools);
    }

    [Fact]
    public async Task GetPluginTools_is_empty_after_stop()
    {
        var (manager, registry) = CreateSystem();
        Register(registry, "withTool", new PluginWithTool());

        await manager.StartAsync("withTool");
        await manager.StopAsync("withTool");

        var tools = manager.GetPluginTools("withTool");
        Assert.Empty(tools);
    }

    [Fact]
    public async Task GetPluginTools_is_empty_for_plugin_without_tools()
    {
        var (manager, registry) = CreateSystem();
        Register(registry, "plain", new MockTestPlugin());

        await manager.StartAsync("plain");

        Assert.Empty(manager.GetPluginTools("plain"));
    }

    [Fact]
    public async Task GetAllMetadata_lists_started_plugin()
    {
        var (manager, registry) = CreateSystem();
        Register(registry, "withTool", new PluginWithTool());

        await manager.StartAsync("withTool");

        var all = manager.GetAllMetadata();
        var meta = Assert.Single(all);
        Assert.Equal("withTool", meta.Id);
        Assert.Equal(PluginState.Running, meta.State);
    }

    [Fact]
    public async Task Start_then_stop_transitions_state_running_to_stopped()
    {
        var (manager, registry) = CreateSystem();
        Register(registry, "plain", new MockTestPlugin());

        await manager.StartAsync("plain");
        Assert.Equal(PluginState.Running, manager.GetMetadata("plain")!.State);

        await manager.StopAsync("plain");
        Assert.Equal(PluginState.Stopped, manager.GetMetadata("plain")!.State);
    }

    [Fact]
    public async Task Start_plugin_that_fails_marks_state_error()
    {
        var (manager, registry) = CreateSystem();
        Register(registry, "error", new ErrorPlugin());

        await Assert.ThrowsAsync<ApplicationException>(() => manager.StartAsync("error"));

        Assert.Equal(PluginState.Error, manager.GetMetadata("error")!.State);
    }

    [Fact]
    public void GetMetadata_returns_null_for_unknown_plugin()
    {
        var (manager, _) = CreateSystem();

        Assert.Null(manager.GetMetadata("unknown"));
    }
}
