using JarvisAI.Application.Agents;
using JarvisAI.Application.Commands;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class AgentSystemTests
{
    private static (Agent agent, CommandRouter router, ToolRegistry registry, ToolExecutor executor) CreateSystem()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var logger = NullLogger<Agent>.Instance;
        var routerLogger = NullLogger<CommandRouter>.Instance;
        var registryLogger = NullLogger<ToolRegistry>.Instance;
        var executorLogger = NullLogger<ToolExecutor>.Instance;

        var registry = new ToolRegistry(registryLogger);
        var executor = new ToolExecutor(registry, eventBus, executorLogger);
        var router = new CommandRouter(routerLogger);
        var agent = new Agent(router, eventBus, logger);

        return (agent, router, registry, executor);
    }

    [Fact]
    public async Task Agent_receives_command_and_returns_success()
    {
        var (agent, router, registry, executor) = CreateSystem();

        var tool = new DateTimeTool();
        registry.Register(tool);

        var command = new TestCommand("time", "Get time", new[] { "time", "heure" }, "date_time");
        var handler = new ToolCommandHandler(executor, NullLogger<ToolCommandHandler>.Instance);
        router.Register(command, handler);

        var context = new AgentContext("time");
        var result = await agent.ProcessAsync(context);

        Assert.True(result.Success);
        Assert.Equal("date_time", result.ToolUsed);
        Assert.False(string.IsNullOrEmpty(result.Response));
    }

    [Fact]
    public void Agent_finds_correct_tool_by_name()
    {
        var (agent, router, registry, executor) = CreateSystem();

        registry.Register(new SystemInfoTool());
        registry.Register(new DateTimeTool());

        var toolNames = registry.GetAll().Select(t => t.Name).ToList();
        Assert.Contains("system_info", toolNames);
        Assert.Contains("date_time", toolNames);
        Assert.Equal(2, toolNames.Count);
    }

    [Fact]
    public async Task Agent_executes_tool_and_returns_output()
    {
        var (agent, router, registry, executor) = CreateSystem();

        registry.Register(new SystemInfoTool());

        var command = new TestCommand("sysinfo", "System info", new[] { "sysinfo", "system" }, "system_info");
        var handler = new ToolCommandHandler(executor, NullLogger<ToolCommandHandler>.Instance);
        router.Register(command, handler);

        var context = new AgentContext("sysinfo");
        var result = await agent.ProcessAsync(context);

        Assert.True(result.Success);
        Assert.Equal("system_info", result.ToolUsed);
        Assert.Contains("OS", result.Response);
    }

    [Fact]
    public async Task Agent_handles_unknown_command_gracefully()
    {
        var (agent, router, registry, executor) = CreateSystem();

        var context = new AgentContext("unknown_command_xyz");
        var result = await agent.ProcessAsync(context);

        Assert.False(result.Success);
        Assert.Contains("No command found", result.Response);
    }

    [Fact]
    public async Task Agent_handles_tool_execution_error()
    {
        var (agent, router, registry, executor) = CreateSystem();

        registry.Register(new FailingTool());
        var command = new TestCommand("fail", "Fail command", new[] { "fail" }, "failing_tool");
        var handler = new ToolCommandHandler(executor, NullLogger<ToolCommandHandler>.Instance);
        router.Register(command, handler);

        var context = new AgentContext("fail");
        var result = await agent.ProcessAsync(context);

        Assert.False(result.Success);
        Assert.Contains("Simulated failure", result.Response);
    }

    [Fact]
    public void ToolRegistry_retrieves_by_category()
    {
        var registryLogger = NullLogger<ToolRegistry>.Instance;
        var registry = new ToolRegistry(registryLogger);

        registry.Register(new SystemInfoTool());
        registry.Register(new DateTimeTool());

        var systemTools = registry.GetByCategory("system");
        Assert.Equal(2, systemTools.Count);
    }

    [Fact]
    public void ToolRegistry_returns_null_for_unknown_tool()
    {
        var registryLogger = NullLogger<ToolRegistry>.Instance;
        var registry = new ToolRegistry(registryLogger);

        var tool = registry.GetByName("nonexistent");
        Assert.Null(tool);
    }

    [Fact]
    public async Task CommandRouter_routes_to_matching_command()
    {
        var routerLogger = NullLogger<CommandRouter>.Instance;
        var router = new CommandRouter(routerLogger);

        var command = new TestCommand("test", "Test command", new[] { "test" });
        var handler = new TestCommandHandler();
        router.Register(command, handler);

        var context = new AgentContext("test");
        var result = await router.RouteAsync(context);

        Assert.True(result.Success);
        Assert.Equal("handled", result.Response);
    }

    [Fact]
    public void CommandRouter_matches_alias()
    {
        var command = new TestCommand("time", "Time", new[] { "time", "heure", "date" });
        Assert.True(command.CanHandle("heure"));
        Assert.True(command.CanHandle("TIME"));
        Assert.False(command.CanHandle("weather"));
    }

    [Fact]
    public void AgentManager_registers_and_selects_agent()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var router = new CommandRouter(NullLogger<CommandRouter>.Instance);
        var agent = new Agent(router, eventBus, NullLogger<Agent>.Instance);

        var manager = new AgentManager(new ServiceProviderForTests(new[] { agent }));
        manager.RegisterAgent(agent);

        Assert.Single(manager.Agents);
    }

    private class ServiceProviderForTests : IServiceProvider
    {
        private readonly IEnumerable<object> _services;
        public ServiceProviderForTests(IEnumerable<object> services) => _services = services;
        public object? GetService(Type serviceType) => _services.FirstOrDefault(s => serviceType.IsInstanceOfType(s));
    }
}

internal sealed class TestCommand : ICommand
{
    public string Name { get; }
    public string Description { get; }
    public string? ToolName { get; }
    public IReadOnlyList<string> Aliases { get; }
    private readonly string _match;

    public TestCommand(string name, string description, string[] aliases, string? toolName = null)
    {
        Name = name;
        Description = description;
        Aliases = aliases;
        ToolName = toolName;
        _match = aliases[0];
    }

    public bool CanHandle(string input)
        => Aliases.Any(a => string.Equals(a, input, StringComparison.OrdinalIgnoreCase));
}

internal sealed class TestCommandHandler : ICommandHandler
{
    public Task<CommandResult> HandleAsync(ICommand command, AgentContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(CommandResult.Succeeded("handled"));
}

internal sealed class FailingTool : ITool
{
    public string Name => "failing_tool";
    public string Description => "A tool that always fails";
    public string Category => "test";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Simulated failure");
}
