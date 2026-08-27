using JarvisAI.Application.AI;
using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Context;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Planning.Strategies;
using JarvisAI.Domain.Events.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Agents;

public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IContextBuilder _contextBuilder;
    private readonly IPlanningStrategySelector _strategySelector;
    private readonly IReasoningLoop _reasoningLoop;
    private readonly IModelRouter _router;
    private readonly IAutomaticMemoryService _memory;
    private readonly IRetryPolicy _retryPolicy;
    private readonly IRunHistory _runHistory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AgentOrchestrator> _logger;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public AgentOrchestrator(
        IContextBuilder contextBuilder,
        IPlanningStrategySelector strategySelector,
        IReasoningLoop reasoningLoop,
        IModelRouter router,
        IAutomaticMemoryService memory,
        IRetryPolicy retryPolicy,
        IRunHistory runHistory,
        IEventBus eventBus,
        ILogger<AgentOrchestrator> logger)
    {
        _contextBuilder = contextBuilder;
        _strategySelector = strategySelector;
        _reasoningLoop = reasoningLoop;
        _router = router;
        _memory = memory;
        _retryPolicy = retryPolicy;
        _runHistory = runHistory;
        _eventBus = eventBus;
        _logger = logger;
    }

    public event EventHandler<AgentRunUpdatedEventArgs>? RunUpdated;

    public async Task<OrchestrationResult> ExecuteAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        var goal = request.Goal?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(goal))
        {
            _logger.LogWarning("[AgentOrchestrator] Refusing empty goal");
            return new OrchestrationResult
            {
                Status = RunStatus.Failed,
                Success = false,
                FinalResponse = "No goal provided.",
                Reason = "Empty goal"
            };
        }

        var run = _runHistory.CreateRun(goal);
        Raise(run, RunStatus.Running);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout ?? DefaultTimeout);
        var runCt = timeoutCts.Token;

        try
        {
            var context = await _contextBuilder.BuildAsync(request, runCt);
            run.SetPlugins(context.ActivePlugins);

            var route = _router.Resolve(goal, conversation: null, request.Mode);
            run.SetModel(route.Model);
            _logger.LogInformation("[AgentOrchestrator] Run {RunId} goal: {Goal} -> model {Model}", run.RunId, goal, route.Model);

            var strategy = _strategySelector.GetStrategy(_strategySelector.Select(goal, context));
            Plan plan;
            try
            {
                plan = await _retryPolicy.ExecuteAsync(
                    ct => strategy.CreatePlanAsync(goal, context, ct),
                    "planning",
                    onRetry: attempt =>
                    {
                        run.IncrementRetries();
                        Raise(run, RunStatus.Running);
                    },
                    runCt);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AgentOrchestrator] Planning failed for {Goal}, using fallback plan", goal);
                plan = CreateFallbackPlan(goal);
            }

            run.SetPlan(plan);
            run.AddStep(
                RunStepKind.Plan,
                $"Plan ({strategy.Name}): {string.Join(" -> ", plan.Steps.Select(s => s.Action))}");
            Raise(run, RunStatus.Running);

            await PublishPlanCreatedAsync(plan, run.RunId);

            var agentContext = new AgentContext(
                request.CommandText ?? goal,
                source: request.Source ?? "agent_orchestrator",
                metadata: request.Metadata is null
                    ? null
                    : request.Metadata.ToDictionary(kv => kv.Key, kv => (object)kv.Value));

            var loopResult = await _reasoningLoop.ExecuteAsync(
                goal,
                context,
                plan,
                route.Model,
                agentContext,
                request.AllowParallelTools,
                onStep: (kind, content, toolName, success, duration) =>
                {
                    run.AddStep(kind, content, toolName, success, duration);
                    _ = PublishStepEventAsync(kind, plan, toolName, content, success, duration, run.RunId);
                    Raise(run, RunStatus.Running);
                },
                runCt);

            run.SetIterations(loopResult.Iterations);

            await SaveOutcomeAsync(goal, loopResult, runCt);

            if (loopResult.Success)
            {
                run.MarkCompleted(loopResult.FinalResponse);
            }
            else if (runCt.IsCancellationRequested)
            {
                runCt.ThrowIfCancellationRequested();
            }
            else
            {
                run.MarkFailed(loopResult.FinalResponse, loopResult.Reason);
            }

            Raise(run, run.Status);
            return OrchestrationResult.FromRun(run, loopResult.FinalResponse, loopResult.Iterations, 0, loopResult.Reason);
        }
        catch (OperationCanceledException)
        {
            var timedOut = !cancellationToken.IsCancellationRequested;
            if (timedOut)
                run.MarkTimedOut();
            else
                run.MarkCancelled("Cancelled by user");

            Raise(run, run.Status);
            return OrchestrationResult.FromRun(run, string.Empty, 0, 0, run.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentOrchestrator] Run {RunId} failed", run.RunId);
            run.MarkFailed(ex.Message, ex.Message);
            Raise(run, run.Status);
            return OrchestrationResult.FromRun(run, string.Empty, 0, 0, ex.Message);
        }
    }

    public RunRecord? GetRun(Guid runId) => _runHistory.Get(runId);

    public IReadOnlyList<RunRecord> GetRecentRuns(int count = 20) => _runHistory.GetRecent(count);

    private async Task SaveOutcomeAsync(string goal, ReasoningLoopResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _memory.SaveObservationAsync(
                $"Goal: {goal}\nResult: {(result.Success ? "SUCCESS" : "PARTIAL")}\n{result.FinalResponse}",
                goal,
                MemoryType.Fact,
                importance: result.Success ? 7 : 4,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentOrchestrator] Failed to save run outcome to memory");
        }
    }

    private void Raise(RunRecord run, RunStatus status)
    {
        try
        {
            RunUpdated?.Invoke(this, new AgentRunUpdatedEventArgs(run.RunId, status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentOrchestrator] RunUpdated handler failed");
        }
    }

    private static Plan CreateFallbackPlan(string goal)
        => new()
        {
            Goal = goal,
            Status = PlanStatus.Created,
            Steps = new List<PlanStep>
            {
                new()
                {
                    Index = 0,
                    Action = "Execute goal",
                    Description = goal,
                    ToolName = null
                }
            }
        };

    private async Task PublishPlanCreatedAsync(Plan plan, Guid runId)
    {
        try
        {
            await _eventBus.PublishAsync(new PlanCreatedEvent(plan.Id, plan.Goal, plan.Steps.Count, runId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentOrchestrator] PlanCreated event publish failed");
        }
    }

    private async Task PublishStepEventAsync(RunStepKind kind, Plan plan, string? toolName, string? content, bool? success, TimeSpan? duration, Guid runId)
    {
        try
        {
            var stepIndex = plan.CurrentStepIndex;
            var action = plan.Steps.Count > stepIndex ? plan.Steps[stepIndex].Action : string.Empty;
            switch (kind)
            {
                case RunStepKind.Plan:
                case RunStepKind.ToolStarted:
                    await _eventBus.PublishAsync(new PlanStepStartedEvent(plan.Id, Math.Max(0, stepIndex), action, toolName, runId));
                    break;
                case RunStepKind.ToolCompleted:
                case RunStepKind.Final:
                case RunStepKind.Error:
                    await _eventBus.PublishAsync(new PlanStepCompletedEvent(plan.Id, Math.Max(0, stepIndex), action, success ?? false, content, duration ?? TimeSpan.Zero, runId));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentOrchestrator] Step event publish failed");
        }
    }
}
