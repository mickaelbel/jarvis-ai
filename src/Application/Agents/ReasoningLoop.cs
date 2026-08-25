using JarvisAI.Application.AI;
using JarvisAI.Application.Context;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Application.Agents;

public sealed class ReasoningLoopOptions
{
    public int MaxIterations { get; init; } = 8;
    public int MaxConsecutiveErrors { get; init; } = 3;
    public int LoopDetectionMaxRepeats { get; init; } = 3;
    public int MaxToolCallsPerTurn { get; init; } = 4;
}

public sealed class LoopTurn
{
    public int Iteration { get; }
    public string? Thought { get; }
    public IReadOnlyList<PendingToolCall> ToolCalls { get; }
    public IReadOnlyList<ToolExecutionOutcome> Outcomes { get; }
    public string? FinalResponse { get; }
    public bool Success { get; }

    public LoopTurn(
        int iteration,
        string? thought,
        IReadOnlyList<PendingToolCall> toolCalls,
        IReadOnlyList<ToolExecutionOutcome> outcomes,
        string? finalResponse,
        bool success)
    {
        Iteration = iteration;
        Thought = thought;
        ToolCalls = toolCalls;
        Outcomes = outcomes;
        FinalResponse = finalResponse;
        Success = success;
    }
}

public sealed class ReasoningLoopResult
{
    public bool Success { get; init; }
    public string FinalResponse { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public IReadOnlyList<LoopTurn> Turns { get; init; } = new List<LoopTurn>();
    public string? Reason { get; init; }
}

public delegate void ReasoningLoopStepHandler(
    RunStepKind kind,
    string content,
    string? toolName = null,
    bool? success = null,
    TimeSpan? duration = null);

public interface IReasoningLoop
{
    Task<ReasoningLoopResult> ExecuteAsync(
        string goal,
        ContextBundle context,
        Plan? plan,
        string? model,
        AgentContext agentContext,
        bool allowParallelTools,
        ReasoningLoopStepHandler? onStep = null,
        CancellationToken cancellationToken = default);
}

public sealed class ReasoningLoop : IReasoningLoop
{
    private readonly IAIProvider _provider;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolSelectionService _toolSelection;
    private readonly IParallelToolExecutor _toolExecutor;
    private readonly IRetryPolicy _retryPolicy;
    private readonly IAutomaticMemoryService _memory;
    private readonly ReasoningLoopOptions _options;
    private readonly ILogger<ReasoningLoop> _logger;

    public ReasoningLoop(
        IAIProvider provider,
        IToolRegistry toolRegistry,
        IToolSelectionService toolSelection,
        IParallelToolExecutor toolExecutor,
        IRetryPolicy retryPolicy,
        IAutomaticMemoryService memory,
        ReasoningLoopOptions options,
        ILogger<ReasoningLoop> logger)
    {
        _provider = provider;
        _toolRegistry = toolRegistry;
        _toolSelection = toolSelection;
        _toolExecutor = toolExecutor;
        _retryPolicy = retryPolicy;
        _memory = memory;
        _options = options;
        _logger = logger;
    }

    public async Task<ReasoningLoopResult> ExecuteAsync(
        string goal,
        ContextBundle context,
        Plan? plan,
        string? model,
        AgentContext agentContext,
        bool allowParallelTools,
        ReasoningLoopStepHandler? onStep = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return Failed(Array.Empty<LoopTurn>(), 0, "Empty goal");

        var systemPrompt = BuildSystemPrompt(goal, plan);
        var conversation = new AIConversation(systemPrompt);
        conversation.AddUserMessage($"{goal}\n\nCONTEXT:\n{context.Render()}");

        var selectedTools = _toolSelection.Select(_toolRegistry.GetAll().Where(t => t.IsAvailable).ToList(), goal, context);
        var toolDefinitions = ToolDefinitionBuilder.Build(selectedTools);

        _logger.LogInformation("[ReasoningLoop] Starting loop for goal: {Goal} (Tools={ToolCount}, Plan={HasPlan}, Model={Model})",
            goal, toolDefinitions.Count, plan is not null, model);

        var turns = new List<LoopTurn>();
        var signatureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var consecutiveErrors = 0;
        var iterations = 0;

        for (var iteration = 1; iteration <= _options.MaxIterations; iteration++)
        {
            iterations = iteration;

            // Escalade auto-modèle : après 2 erreurs consécutives, injecte un
            // hint dans le prompt pour que le modèle propose un modèle plus
            // puissant (lien avec l'outil gestion_modeles/changer_modele).
            var currentPrompt = systemPrompt;
            if (consecutiveErrors >= 2)
                currentPrompt += "\n\n⚠ IMPORTANT : le modèle actuel est en difficulté (échecs répétés). " +
                    "Tu DOIS proposer un modèle plus adapté à l'utilisateur en appelant " +
                    "l'outil gestion_modeles (action=suggere) ou changer_modele (action=proposer). " +
                    "Ne réessaie pas avec le même modèle.";

            var request = new AIRequest(currentPrompt, conversation.ToRequestMessages(), toolDefinitions, model, temperature: 0.2f);

            AIResponse response;
            try
            {
                response = await _retryPolicy.ExecuteAsync(
                    ct => _provider.ChatAsync(request, ct),
                    $"reasoning iteration {iteration}",
                    onRetry: attempt => onStep?.Invoke(
                        RunStepKind.Error,
                        $"Retrying LLM call ({attempt.Attempt}/{attempt.TotalAttempts}): {attempt.Exception?.Message}"),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Failed(turns, iterations, "Cancelled");
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                onStep?.Invoke(RunStepKind.Error, $"LLM call failed: {ex.Message}");
                _logger.LogWarning(ex, "[ReasoningLoop] LLM call failed on iteration {Iteration}", iteration);
                if (consecutiveErrors >= _options.MaxConsecutiveErrors)
                    return Failed(turns, iterations, $"Repeated LLM errors: {ex.Message}");
                continue;
            }

            if (!response.Success)
            {
                consecutiveErrors++;
                onStep?.Invoke(RunStepKind.Error, $"LLM error: {response.ErrorMessage}");
                if (consecutiveErrors >= _options.MaxConsecutiveErrors)
                    return Failed(turns, iterations, $"Repeated LLM errors: {response.ErrorMessage}");
                continue;
            }
            consecutiveErrors = 0;

            onStep?.Invoke(RunStepKind.Thought, string.IsNullOrWhiteSpace(response.Content) ? "(thinking...)" : response.Content);

            if (response.HasToolCalls)
            {
                var calls = response.ToolCalls
                    .Take(_options.MaxToolCallsPerTurn)
                    .Select(tc => new PendingToolCall(tc.Name, tc.Arguments, tc.Id))
                    .ToList();

                foreach (var call in calls)
                {
                    var signature = Signature(call);
                    signatureCounts.TryGetValue(signature, out var count);
                    signatureCounts[signature] = count + 1;
                    if (count + 1 >= _options.LoopDetectionMaxRepeats)
                    {
                        onStep?.Invoke(RunStepKind.Error, $"Loop detected: repeated tool call {call.Name}");
                        return Failed(turns, iterations, $"Loop detected: tool call '{call.Name}' repeated without progress");
                    }
                }

                conversation.AddAssistantWithToolCalls(
                    response.Content ?? string.Empty,
                    calls.Select(c => new AIToolCall(c.Id, c.Name, c.Arguments)).ToList());

                foreach (var call in calls)
                    onStep?.Invoke(RunStepKind.ToolStarted, $"Calling {call.Name}", call.Name);

                IReadOnlyList<ToolExecutionOutcome> outcomes;
                try
                {
                    outcomes = await _toolExecutor.ExecuteAsync(calls, agentContext, allowParallelTools, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return Failed(turns, iterations, "Cancelled");
                }

                foreach (var (call, outcome) in calls.Zip(outcomes))
                {
                    var resultContent = outcome.Success ? outcome.Output : $"Error: {outcome.ErrorMessage}";
                    conversation.AddToolResult(call.Id, call.Name, resultContent);
                    onStep?.Invoke(
                        RunStepKind.ToolCompleted,
                        resultContent,
                        call.Name,
                        outcome.Success,
                        outcome.Duration);

                    await _memory.ConsiderSaveAsync(resultContent, goal, MemoryType.ToolResult, cancellationToken);
                }

                var hasSuccessfulOpenReasoning = outcomes.Any(o =>
                    o.Success && o.Output is not null &&
                    (o.Output.Contains("ACTION TERMINÉE", StringComparison.OrdinalIgnoreCase) ||
                     o.Output.Contains("Ouvert dans le navigateur", StringComparison.OrdinalIgnoreCase)));
                if (hasSuccessfulOpenReasoning)
                {
                    conversation.AddMessage(AIMessage.System(
                        "UN OUTIL A RÉUSSI ET A OUVERT/LANCÉ QUELQUE CHOSE. TA TÂCHE EST TERMINÉE. " +
                        "N'appelle AUCUN autre outil. Donne ta réponse finale MAINTENANT en décrivant ce qui a été fait."));
                }

                turns.Add(new LoopTurn(iterations, response.Content, calls, outcomes, null, false));
                continue;
            }

            var final = response.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(final))
            {
                conversation.AddMessage(AIMessage.System("Your previous response was empty. Provide a final answer or a tool call to continue."));
                continue;
            }

            conversation.AddAssistantMessage(final);
            onStep?.Invoke(RunStepKind.Final, final);
            _logger.LogInformation("[ReasoningLoop] Final answer produced on iteration {Iteration}", iterations);

            turns.Add(new LoopTurn(iterations, final, Array.Empty<PendingToolCall>(), Array.Empty<ToolExecutionOutcome>(), final, true));
            return new ReasoningLoopResult
            {
                Success = true,
                FinalResponse = final,
                Iterations = iterations,
                Turns = turns,
                Reason = null
            };
        }

        return Failed(turns, iterations, $"Maximum iterations ({_options.MaxIterations}) reached");
    }

    private static string BuildSystemPrompt(string goal, Plan? plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Jarvis, an autonomous reasoning agent using the ReAct loop: you THINK, ACT with a tool, OBSERVE the result, and repeat until the goal is achieved, then answer FINAL.");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Emit tool calls with the exact tool names and parameters from the provided tool definitions.");
        sb.AppendLine("- Wait for each tool result before deciding the next action.");
        sb.AppendLine("- Do not repeat a tool call with the same arguments if it already failed without changes.");
        sb.AppendLine("- If a tool fails, explain the error and try a different approach or stop.");
        sb.AppendLine("- When the goal is fully achieved, respond ONLY with the final answer in the user's language.");

        if (plan is not null && plan.Steps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("FOLLOW THIS PLAN (execute the steps in order):");
            foreach (var step in plan.Steps)
                sb.AppendLine($"- Step {step.Index + 1}: {step.Action} (tool: {step.ToolName ?? "none"}) - {step.Description}");
        }

        return sb.ToString();
    }

    private static string Signature(PendingToolCall call)
    {
        var args = call.Arguments
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}");
        return $"{call.Name}|{string.Join("&", args)}";
    }

    private static ReasoningLoopResult Failed(IReadOnlyList<LoopTurn> turns, int iterations, string reason)
        => new()
        {
            Success = false,
            FinalResponse = string.Empty,
            Iterations = iterations,
            Turns = turns,
            Reason = reason
        };
}
