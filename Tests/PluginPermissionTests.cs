using JarvisAI.Application.Agents;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Plugins;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security;

namespace JarvisAI.Tests;

public sealed class PluginPermissionTests
{
    private static ITool RiskyTool(string name, SecurityRiskLevel risk)
        => new DummyPluginTool(name, risk);

    // ================================================================
    // PluginPermissionManager / PluginPermissionCatalog
    // ================================================================

    [Fact]
    public void Unknown_permission_is_high_risk()
    {
        Assert.Equal(SecurityRiskLevel.High, PluginPermissionCatalog.GetRequiredRisk("totally_unknown_tool"));
        Assert.False(PluginPermissionCatalog.IsKnown("totally_unknown_tool"));
    }

    [Theory]
    [InlineData("system_info", SecurityRiskLevel.Low)]
    [InlineData("read_only", SecurityRiskLevel.Low)]
    [InlineData("process_list", SecurityRiskLevel.Medium)]
    [InlineData("file_write", SecurityRiskLevel.Medium)]
    [InlineData("network", SecurityRiskLevel.Medium)]
    [InlineData("terminal", SecurityRiskLevel.High)]
    [InlineData("file_delete", SecurityRiskLevel.High)]
    [InlineData("screen_capture", SecurityRiskLevel.High)]
    public void Known_permissions_have_expected_risk(string permission, SecurityRiskLevel expected)
    {
        Assert.Equal(expected, PluginPermissionCatalog.KnownPermissions[permission]);
        Assert.True(PluginPermissionCatalog.IsKnown(permission));
    }

    [Fact]
    public void Low_permission_plugin_cannot_register_any_risky_tool()
    {
        var metadata = new PluginMetadata { PermissionLevel = PluginPermissionLevel.Low };
        var manager = new PluginPermissionManager();

        Assert.True(manager.CanRegisterTool(metadata, RiskyTool("t", SecurityRiskLevel.Low)));
        Assert.False(manager.CanRegisterTool(metadata, RiskyTool("t", SecurityRiskLevel.Medium)));
        Assert.False(manager.CanRegisterTool(metadata, RiskyTool("t", SecurityRiskLevel.High)));
    }

    [Fact]
    public void Medium_permission_plugin_can_register_medium_but_not_high()
    {
        var metadata = new PluginMetadata { PermissionLevel = PluginPermissionLevel.Medium };
        var manager = new PluginPermissionManager();

        Assert.True(manager.CanRegisterTool(metadata, RiskyTool("t", SecurityRiskLevel.Low)));
        Assert.True(manager.CanRegisterTool(metadata, RiskyTool("t", SecurityRiskLevel.Medium)));
        Assert.False(manager.CanRegisterTool(metadata, RiskyTool("t", SecurityRiskLevel.High)));
    }

    [Fact]
    public void High_permission_plugin_can_register_anything()
    {
        var metadata = new PluginMetadata { PermissionLevel = PluginPermissionLevel.High };
        var manager = new PluginPermissionManager();

        Assert.True(manager.CanRegisterTool(metadata, RiskyTool("t", SecurityRiskLevel.Low)));
        Assert.True(manager.CanRegisterTool(metadata, RiskyTool("t", SecurityRiskLevel.Medium)));
        Assert.True(manager.CanRegisterTool(metadata, RiskyTool("t", SecurityRiskLevel.High)));
    }

    [Fact]
    public void IsRiskAllowed_and_MaxAllowedRisk_are_consistent()
    {
        Assert.Equal(SecurityRiskLevel.Low, PluginPermissionManager.MaxAllowedRisk(PluginPermissionLevel.Low));
        Assert.Equal(SecurityRiskLevel.Medium, PluginPermissionManager.MaxAllowedRisk(PluginPermissionLevel.Medium));
        Assert.Equal(SecurityRiskLevel.High, PluginPermissionManager.MaxAllowedRisk(PluginPermissionLevel.High));

        Assert.True(PluginPermissionManager.IsRiskAllowed(SecurityRiskLevel.High, PluginPermissionLevel.High));
        Assert.False(PluginPermissionManager.IsRiskAllowed(SecurityRiskLevel.High, PluginPermissionLevel.Medium));
        Assert.False(PluginPermissionManager.IsRiskAllowed(SecurityRiskLevel.Medium, PluginPermissionLevel.Low));
    }

    [Fact]
    public void GetUnknownPermissions_reports_only_unknown_permissions()
    {
        var metadata = new PluginMetadata
        {
            Permissions = new() { "system_info", "some_mystery_tool", "process_list" }
        };
        var manager = new PluginPermissionManager();

        var unknown = manager.GetUnknownPermissions(metadata);

        Assert.Equal(new[] { "some_mystery_tool" }, unknown);
    }

    // ================================================================
    // PluginContext.RegisterTool enforcement
    // ================================================================

    [Fact]
    public void Low_permission_plugin_context_denies_high_risk_tool()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var toolRegistry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var metadata = new PluginMetadata { PermissionLevel = PluginPermissionLevel.Low };
        var context = new PluginContext(
            eventBus,
            toolRegistry,
            new UnsupportedServiceProvider(),
            "Plugins",
            metadata,
            new PluginPermissionManager());

        var ex = Assert.Throws<SecurityException>(() =>
            context.RegisterTool(RiskyTool("delete_all_files", SecurityRiskLevel.High)));

        Assert.Contains("permission", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(toolRegistry.GetByName("delete_all_files"));
    }

    [Fact]
    public void Medium_permission_plugin_context_denies_high_but_allows_medium()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var toolRegistry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var metadata = new PluginMetadata { PermissionLevel = PluginPermissionLevel.Medium };
        var context = new PluginContext(
            eventBus,
            toolRegistry,
            new UnsupportedServiceProvider(),
            "Plugins",
            metadata,
            new PluginPermissionManager());

        Assert.Throws<SecurityException>(() =>
            context.RegisterTool(RiskyTool("terminal_use", SecurityRiskLevel.High)));

        context.RegisterTool(RiskyTool("network_tool", SecurityRiskLevel.Medium));

        Assert.Null(toolRegistry.GetByName("terminal_use"));
        Assert.NotNull(toolRegistry.GetByName("network_tool"));
    }

    [Fact]
    public void High_permission_plugin_context_registers_high_risk_tool()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var toolRegistry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var metadata = new PluginMetadata { PermissionLevel = PluginPermissionLevel.High };
        var context = new PluginContext(
            eventBus,
            toolRegistry,
            new UnsupportedServiceProvider(),
            "Plugins",
            metadata,
            new PluginPermissionManager());

        context.RegisterTool(RiskyTool("dangerous_but_authorized", SecurityRiskLevel.High));

        Assert.NotNull(toolRegistry.GetByName("dangerous_but_authorized"));
    }

    [Fact]
    public void Plugin_context_without_permission_manager_registers_tool_unconditionally()
    {
        // Backward compatibility: an optional permission manager must not break
        // existing plugins that construct a context directly.
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var toolRegistry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var context = new PluginContext(eventBus, toolRegistry, new UnsupportedServiceProvider(), "Plugins");

        context.RegisterTool(RiskyTool("legacy_tool", SecurityRiskLevel.High));

        Assert.NotNull(toolRegistry.GetByName("legacy_tool"));
    }

    // ================================================================
    // PluginManager wiring + real plugin loading
    // ================================================================

    [Fact]
    public async Task PluginManager_loads_system_control_and_respects_its_permission_level()
    {
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var toolRegistry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);

        var manager = new PluginManager(
            loader,
            registry,
            eventBus,
            toolRegistry,
            new UnsupportedServiceProvider(),
            NullLogger<PluginManager>.Instance,
            pluginsDir,
            new PluginPermissionManager());

        await manager.DiscoverAsync(pluginsDir);
        await manager.StartAllAsync();

        var metadata = manager.GetMetadata("JarvisAI.Plugins.SystemControl");
        Assert.NotNull(metadata);
        Assert.Equal(PluginPermissionLevel.Medium, metadata.PermissionLevel);
        Assert.Equal(PluginState.Running, registry.Get("JarvisAI.Plugins.SystemControl")!.State);

        // The SystemControl plugin registers Low-risk tools through the secured
        // context; they are within the Medium grant, so they must be present.
        Assert.NotNull(toolRegistry.GetByName("sys_system_info"));
        Assert.NotNull(toolRegistry.GetByName("sys_process_list"));
    }

    private sealed class DummyPluginTool : ITool
    {
        public DummyPluginTool(string name, SecurityRiskLevel riskLevel)
        {
            Name = name;
            RiskLevel = riskLevel;
        }

        public string Name { get; }
        public string Description => "Dummy plugin tool for permission tests";
        public string Category => "test";
        public SecurityRiskLevel RiskLevel { get; }
        public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        public Task<ToolResult> ExecuteAsync(
            AgentContext context,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Succeeded("done"));
    }

    private sealed class UnsupportedServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
