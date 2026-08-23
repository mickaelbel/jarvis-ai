using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Events.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Planning;

public sealed class PlanExecutor
{
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly IEventBus _eventBus;
    private readonly ILogger<PlanExecutor> _logger;

    public PlanExecutor(
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        IEventBus eventBus,
        ILogger<PlanExecutor> logger)
    {
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<PlanExecutionResult> ExecuteAsync(Plan plan, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[PlanExecutor] Executing plan {PlanId} with {StepCount} steps: {Goal}",
            plan.Id, plan.Steps.Count, plan.Goal);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        plan.MarkInProgress();

        await _eventBus.PublishAsync(
            new PlanCreatedEvent(plan.Id, plan.Goal, plan.Steps.Count, plan.CorrelationId),
            cancellationToken);

        var stepsCompleted = 0;

        foreach (var step in plan.Steps)
        {
            step.Status = PlanStepStatus.InProgress;

            await _eventBus.PublishAsync(
                new PlanStepStartedEvent(plan.Id, step.Index, step.Action, step.ToolName, plan.CorrelationId),
                cancellationToken);

            _logger.LogInformation("[PlanExecutor] Step {Index}: {Action} (Tool={Tool})",
                step.Index, step.Action, step.ToolName ?? "none");

            var stepSw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (step.ToolName != null)
                {
                    var tool = _toolRegistry.GetByName(step.ToolName);
                    if (tool == null)
                    {
                        step.Status = PlanStepStatus.Failed;
                        step.ErrorMessage = $"Tool '{step.ToolName}' not found in registry";
                        _logger.LogWarning("[PlanExecutor] Tool not found: {ToolName}", step.ToolName);

                        plan.MarkFailed(step.ErrorMessage);
                        stopwatch.Stop();

                        await _eventBus.PublishAsync(
                            new PlanStepCompletedEvent(plan.Id, step.Index, step.Action, false, step.ErrorMessage, stepSw.Elapsed, plan.CorrelationId),
                            cancellationToken);

                        await _eventBus.PublishAsync(
                            new PlanFailedEvent(plan.Id, step.ErrorMessage, step.Index, plan.CorrelationId),
                            cancellationToken);

                        return PlanExecutionResult.FailureResult(plan.Id, stepsCompleted, plan.Steps.Count, step.ErrorMessage, stopwatch.Elapsed);
                    }

                    var context = new AgentContext(
                        step.ToolName,
                        source: "plan_executor",
                        new Dictionary<string, object>
                        {
                            ["planId"] = plan.Id,
                            ["stepIndex"] = step.Index,
                            ["parameters"] = step.Parameters
                        });

                    var result = await _toolExecutor.ExecuteAsync(step.ToolName, context, cancellationToken);

                    stepSw.Stop();
                    step.Duration = stepSw.Elapsed;

                    if (result.Success)
                    {
                        step.Status = PlanStepStatus.Completed;
                        step.Result = result.Output;
                        stepsCompleted++;

                        _logger.LogInformation("[PlanExecutor] Step {Index} completed: {Result}",
                            step.Index, result.Output?.Length > 100 ? result.Output[..100] + "..." : result.Output);
                    }
                    else
                    {
                        step.Status = PlanStepStatus.Failed;
                        step.ErrorMessage = result.ErrorMessage;

                        plan.MarkFailed(result.ErrorMessage);
                        stopwatch.Stop();

                        await _eventBus.PublishAsync(
                            new PlanStepCompletedEvent(plan.Id, step.Index, step.Action, false, result.ErrorMessage, stepSw.Elapsed, plan.CorrelationId),
                            cancellationToken);

                        await _eventBus.PublishAsync(
                            new PlanFailedEvent(plan.Id, result.ErrorMessage, step.Index, plan.CorrelationId),
                            cancellationToken);

                        return PlanExecutionResult.FailureResult(plan.Id, stepsCompleted, plan.Steps.Count, result.ErrorMessage, stopwatch.Elapsed);
                    }
                }
                else
                {
                    stepSw.Stop();
                    step.Duration = stepSw.Elapsed;
                    step.Status = PlanStepStatus.Completed;
                    step.Result = $"Action '{step.Action}' acknowledged (no tool required)";
                    stepsCompleted++;

                    _logger.LogInformation("[PlanExecutor] Step {Index} completed (no tool): {Action}",
                        step.Index, step.Action);
                }

                await _eventBus.PublishAsync(
                    new PlanStepCompletedEvent(plan.Id, step.Index, step.Action, true, step.Result, step.Duration, plan.CorrelationId),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                stepSw.Stop();
                step.Duration = stepSw.Elapsed;
                step.Status = PlanStepStatus.Failed;
                step.ErrorMessage = ex.Message;

                _logger.LogError(ex, "[PlanExecutor] Step {Index} failed with exception", step.Index);

                plan.MarkFailed(ex.Message);
                stopwatch.Stop();

                await _eventBus.PublishAsync(
                    new PlanStepCompletedEvent(plan.Id, step.Index, step.Action, false, ex.Message, step.Duration, plan.CorrelationId),
                    cancellationToken);

                await _eventBus.PublishAsync(
                    new PlanFailedEvent(plan.Id, ex.Message, step.Index, plan.CorrelationId),
                    cancellationToken);

                return PlanExecutionResult.FailureResult(plan.Id, stepsCompleted, plan.Steps.Count, ex.Message, stopwatch.Elapsed);
            }
        }

        stopwatch.Stop();
        plan.MarkCompleted($"All {stepsCompleted} steps completed successfully");

        _logger.LogInformation("[PlanExecutor] Plan {PlanId} completed successfully ({Steps}/{Total} steps)",
            plan.Id, stepsCompleted, plan.Steps.Count);

        return PlanExecutionResult.SuccessResult(plan.Id, stepsCompleted, plan.Steps.Count, plan.Summary, stopwatch.Elapsed);
    }

    public async Task<PlanExecutionResult> ExecuteWithCancellationAsync(Plan plan, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(plan, cancellationToken);
    }
}
