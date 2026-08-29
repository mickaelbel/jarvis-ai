using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Delegation;

/// <summary>
/// Récupère le résultat d'une tâche déléguée à un subagent.
/// </summary>
public sealed class GetSubagentResultTool : ITool
{
    private readonly DelegationService _delegation;
    private readonly ILogger<GetSubagentResultTool> _logger;

    public string Name => "get_subagent_result";
    public string Description =>
        "Récupérer le résultat d'une tâche déléguée à un subagent. " +
        "Appelle ceci avec le taskId retourné par delegate_task.";
    public string Category => "meta";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("task_id", "ID de la tâche déléguée (retourné par delegate_task)", typeof(string), required: true),
    };

    public GetSubagentResultTool(DelegationService delegation, ILogger<GetSubagentResultTool> logger)
    {
        _delegation = delegation;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("task_id", out var taskId);
        if (string.IsNullOrWhiteSpace(taskId))
            return ToolResult.Failed("Paramètre 'task_id' requis.");

        var result = await _delegation.GetResultAsync(taskId, cancellationToken);

        if (result.Success)
            return ToolResult.Succeeded($"[Subagent {taskId}] {result.Output}");
        else
            return ToolResult.Failed($"[Subagent {taskId}] Échec: {result.Output}");
    }
}
