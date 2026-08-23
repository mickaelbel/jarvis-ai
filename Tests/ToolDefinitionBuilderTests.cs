using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class ToolDefinitionBuilderTests
{
    [Fact]
    public void Build_maps_all_registered_tools()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new DateTimeTool());
        registry.Register(new SystemInfoTool());

        var definitions = ToolDefinitionBuilder.Build(registry);

        Assert.Equal(2, definitions.Count);
        Assert.Contains(definitions, d => d.Name == "date_time");
        Assert.Contains(definitions, d => d.Name == "system_info");
    }

    [Fact]
    public void Build_maps_parameters_and_required_flags()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new RequiredParamTool());

        var definitions = ToolDefinitionBuilder.Build(registry);

        var tool = Assert.Single(definitions);
        Assert.Equal("required_param_tool", tool.Name);
        Assert.Contains("path", tool.Properties.Keys);
        Assert.Equal("string", tool.Properties["path"].Type);
        Assert.Contains("path", tool.Required);
        Assert.Contains("optional", tool.Properties.Keys);
        Assert.DoesNotContain("optional", tool.Required);
    }

    [Fact]
    public void Build_returns_empty_when_no_tools()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);

        var definitions = ToolDefinitionBuilder.Build(registry);

        Assert.Empty(definitions);
    }

    private sealed class RequiredParamTool : ITool
    {
        public string Name => "required_param_tool";
        public string Description => "Tool with a required parameter";
        public string Category => "test";
        public IReadOnlyList<ToolParameter> Parameters => new[]
        {
            new ToolParameter("path", "File path", typeof(string), required: true),
            new ToolParameter("optional", "Optional flag", typeof(bool))
        };

        public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Succeeded("ok"));
    }
}
