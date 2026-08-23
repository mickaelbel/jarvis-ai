using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

public sealed class SystemInfoTool : ITool
{
    public string Name => "system_info";
    public string Description => "Returns system information: OS, processor, memory, runtime";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public bool McpExpose => true;

    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var info = new
        {
            OS = Environment.OSVersion.ToString(),
            MachineName = Environment.MachineName,
            ProcessorCount = Environment.ProcessorCount,
            WorkingSet = $"{Environment.WorkingSet / 1024 / 1024} MB",
            RuntimeVersion = Environment.Version.ToString(),
            Is64Bit = Environment.Is64BitOperatingSystem,
            UserDomain = Environment.UserDomainName,
            UserName = Environment.UserName
        };

        return Task.FromResult(ToolResult.Succeeded(System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
    }
}

