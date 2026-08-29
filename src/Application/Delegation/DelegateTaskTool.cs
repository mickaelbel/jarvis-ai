using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Delegation;

/// <summary>
/// Outil de délégation : l'agent principal délégue une sous-tâche à un
/// subagent isolé qui s'exécute en parallèle. Résultat retourné quand disponible.
/// </summary>
public sealed class DelegateTaskTool : ITool
{
    private readonly DelegationService _delegation;
    private readonly ILogger<DelegateTaskTool> _logger;

    public string Name => "delegate_task";
    public string Description =>
        "Déléguer une tâche à un subagent isolé qui s'exécute en parallèle. " +
        "Utilise ceci pour des recherches, analyses, ou tâches indépendantes " +
        "qui peuvent tourner en fond pendant que tu continues.";
    public string Category => "meta";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "Subagent lancé…";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("task", "Description de la tâche à déléguer", typeof(string), required: true),
        new ToolParameter("tool_hint", "Nom de l'outil à exécuter directement (optionnel)", typeof(string), required: false),
    };

    public DelegateTaskTool(DelegationService delegation, ILogger<DelegateTaskTool> logger)
    {
        _delegation = delegation;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("task", out var task);
        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Failed("Paramètre 'task' requis.");

        parameters.TryGetValue("tool_hint", out var toolHint);

        if (_delegation.ActiveCount >= _delegation.MaxConcurrent)
            return ToolResult.Failed($"Limite de subagents atteinte ({_delegation.MaxConcurrent}). Réessaie plus tard.");

        var taskId = await _delegation.DelegateAsync(task, string.IsNullOrWhiteSpace(toolHint) ? null : toolHint, cancellationToken);

        _logger.LogInformation("[DelegateTask] Tâche {TaskId} déléguée: {Task}", taskId, task);
        return ToolResult.Succeeded($"Subagent {taskId} lancé pour: {task}. Utilise get_subagent_result pour récupérer le résultat.");
    }
}
