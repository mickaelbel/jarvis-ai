using JarvisAI.Application.Agents;

namespace JarvisAI.Application.Tools;

public interface IToolExecutor
{
    Task<ToolResult> ExecuteAsync(string toolName, AgentContext context, CancellationToken cancellationToken = default);
}
