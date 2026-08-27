using JarvisAI.Application.Abstractions;
using JarvisAI.Application.AI;
using JarvisAI.Application.Context;
using JarvisAI.Application.Planning.Strategies;
using JarvisAI.Domain.Events.Agents;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Agents.Supervision;

/// <summary>Résultat d'un run supervisé.</summary>
public sealed record AgentSupervisionResult
{
    public Guid RunId { get; init; }
    public bool Success { get; init; }
    public string FinalResponse { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public AgentKind AgentKind { get; init; }
    public int TaskCount { get; init; }
    public int CompletedTasks { get; init; }
    public bool Verified { get; init; }
    public string? Correction { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Superviseur central : reçoit l'objectif utilisateur, construit le contexte,
/// sélectionne l'agent spécialisé, décompose en tâches, les exécute (parallélisées
/// si indépendantes) avec reprise après erreur, vérifie le résultat et publie les
/// événements de cycle de vie sur le bus central.
/// Point d'entrée : Goal → Perception → Planning → Tool → Observation → Verification
/// → Replanning → Goal completed.
/// </summary>
public interface IAgentSupervisor
{
    Task<AgentSupervisionResult> ExecuteAsync(AgentRequest request, CancellationToken cancellationToken = default);
}

public sealed class AgentSupervisor : IAgentSupervisor
{
    private readonly IContextBuilder _contextBuilder;
    private readonly ISpecializedAgentSelector _agentSelector;
    private readonly IMultiAgentOrchestrator _multiAgent;
    private readonly IAgentVerifier _verifier;
    private readonly TaskExecutor _taskExecutor;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AgentSupervisor> _logger;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public AgentSupervisor(
        IContextBuilder contextBuilder,
        ISpecializedAgentSelector agentSelector,
        IMultiAgentOrchestrator multiAgent,
        IAgentVerifier verifier,
        TaskExecutor taskExecutor,
        IEventBus eventBus,
        ILogger<AgentSupervisor> logger)
    {
        _contextBuilder = contextBuilder;
        _agentSelector = agentSelector;
        _multiAgent = multiAgent;
        _verifier = verifier;
        _taskExecutor = taskExecutor;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<AgentSupervisionResult> ExecuteAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        var goal = request.Goal?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(goal))
            return new AgentSupervisionResult { Success = false, Reason = "Empty goal" };

        var started = DateTime.UtcNow;
        var runId = request.CorrelationId ?? Guid.NewGuid();
        var correlationId = runId;

        // ── Perception ─────────────────────────────────────────────────────────
        var context = await _contextBuilder.BuildAsync(request, cancellationToken);

        // ── Agent spécialisé ───────────────────────────────────────────────────
        var agent = _agentSelector.Select(goal, context);
        await _eventBus.PublishAsync(new AgentStartedEvent(runId, goal, agent.Kind.ToString(), correlationId), cancellationToken);
        _logger.LogInformation("[Supervisor] Agent {Kind} sélectionné pour : {Goal}", agent.Kind, goal);

        // ── Planning (graphe de tâches) + Agent Loop ───────────────────────────
        var graph = BuildTaskGraph(goal, agent);

        var results = new List<AgentTaskResult>();
        var completed = 0;
        var failed = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.Timeout ?? DefaultTimeout);

        await _taskExecutor.RunAsync(
            graph,
            async (task, ct) =>
            {
                var taskResult = await ExecuteTaskAsync(task, request, ct);
                results.Add(taskResult);
                return (taskResult.Success, taskResult.FinalResponse, taskResult.Reason);
            },
            parallelize: true,
            onStatus: (task, status) =>
            {
                if (status == AgentTaskStatus.Completed) completed++;
                if (status is AgentTaskStatus.Failed or AgentTaskStatus.Aborted) failed++;
            },
            cts.Token);

        // ── Synthesis ──────────────────────────────────────────────────────────
        var successTasks = results.Count(r => r.Success);
        var finalResponse = results.Count == 1
            ? results[0].FinalResponse
            : SynthesizeLocal(goal, results);

        // ── Verification + Recovery ────────────────────────────────────────────
        var verdict = await _verifier.VerifyAsync(
            goal,
            finalResponse,
            $"Tasks done: {successTasks}/{results.Count}",
            cancellationToken: cancellationToken);

        if (!verdict.Verified && !string.IsNullOrWhiteSpace(verdict.Correction) && successTasks == 0)
        {
            _logger.LogWarning("[Supervisor] Non vérifié, replanification : {Correction}", verdict.Correction);
            try
            {
                var retry = await _multiAgent.ExecuteAsync(goal, request.Mode, cancellationToken);
                if (retry.Success && !string.IsNullOrWhiteSpace(retry.FinalResponse))
                    finalResponse = retry.FinalResponse;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Supervisor] Replanification échouée");
            }
        }

        var success = successTasks > 0 || verdict.Verified;
        await _eventBus.PublishAsync(new AgentCompletedEvent(
            runId, success, failed > 0 ? $"{failed} tâche(s) en échec" : null, results.Count, correlationId), cancellationToken);

        return new AgentSupervisionResult
        {
            RunId = runId,
            Success = success,
            FinalResponse = finalResponse,
            Reason = failed > 0 ? $"{failed} tâche(s) en échec" : null,
            AgentKind = agent.Kind,
            TaskCount = results.Count,
            CompletedTasks = successTasks,
            Verified = verdict.Verified,
            Correction = verdict.Correction,
            Duration = DateTime.UtcNow - started
        };
    }

    private static AgentTaskGraph BuildTaskGraph(string goal, SpecializedAgentDescriptor agent)
    {
        var graph = new AgentTaskGraph();

        // Objectif général / simple ou agent spécialisé → une seule tâche portée
        // par le multi-agent (qui gère lui-même simple vs parallèle). Le graphe
        // reste la couche de reprise/retry et d'observabilité.
        var title = agent.Kind == AgentKind.General
            ? "Exécuter l'objectif"
            : $"Agent {agent.Name}";
        graph.Add(title, goal);
        return graph;
    }

    private async Task<AgentTaskResult> ExecuteTaskAsync(AgentTask task, AgentRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _multiAgent.ExecuteAsync(task.Goal, request.Mode, ct);
            return new AgentTaskResult(task.Title, result.Success, result.FinalResponse, result.Reason, result.SubAgents);
        }
        catch (OperationCanceledException)
        {
            return new AgentTaskResult(task.Title, false, string.Empty, "Cancelled", 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Supervisor] Tâche {Title} en erreur", task.Title);
            return new AgentTaskResult(task.Title, false, string.Empty, ex.Message, 0);
        }
    }

    private string SynthesizeLocal(string goal, IReadOnlyList<AgentTaskResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Objectif : {goal}");
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine($"────────── {r.Title} ──────────");
            sb.AppendLine(r.Success ? r.FinalResponse : $"(échec : {r.Reason})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private sealed record AgentTaskResult(string Title, bool Success, string FinalResponse, string? Reason, int SubAgents);
}
