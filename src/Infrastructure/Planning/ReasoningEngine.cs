using System.Text.Json;
using JarvisAI.Application.AI;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Reasoning;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Planning;

public sealed class ReasoningEngine : IReasoningEngine
{
    private readonly AIService _aiService;
    private readonly ILogger<ReasoningEngine> _logger;
    private readonly IMemoryService? _memory;

    private const int MaxThoughts = 10;

    private const string ReasoningSystemPrompt = @"You are a reasoning engine. Analyze goals step by step using structured thinking.

For each thought, respond with ONLY a JSON object:
{
  ""type"": ""<Analysis|Decision|Observation|Planning|Reflection|Conclusion>"",
  ""content"": ""<your thought>"",
  ""conclusion"": ""<conclusion if any, or null>""
}

Use Analysis to break down the problem.
Use Decision to choose between options.
Use Observation to note facts.
Use Planning to outline steps.
Use Reflection to evaluate progress.
Use Conclusion to finalize reasoning.

Be concise. Max 5-8 thoughts. Think in French.";

    public ReasoningEngine(AIService aiService, ILogger<ReasoningEngine> logger, IMemoryService? memory = null)
    {
        _aiService = aiService;
        _logger = logger;
        _memory = memory;
    }

    public async Task<ReasoningContext> AnalyzeGoalAsync(string goal, IReadOnlyList<string> availableTools, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[ReasoningEngine] Analyzing goal: {Goal}", goal);

        var context = new ReasoningContext
        {
            Goal = goal,
            AvailableTools = availableTools.ToList()
        };

        var toolList = string.Join(", ", availableTools);
        var memoryBlock = await BuildMemoryBlockAsync(goal, cancellationToken);
        var userMessage = $"Analyze this goal and break it down:\n\nGoal: {goal}\nAvailable tools: {toolList}{memoryBlock}\n\nStart with your initial analysis.";

        var thought = await GenerateThoughtAsync(userMessage, cancellationToken);
        context.AddThought(thought);

        return context;
    }

    public async Task<ThoughtStep> NextThoughtAsync(ReasoningContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[ReasoningEngine] Generating next thought (step {StepIndex})", context.Thoughts.Count);

        // Limiter le nombre de pensées pour éviter les boucles infinies
        if (context.Thoughts.Count >= MaxThoughts)
        {
            _logger.LogWarning("[ReasoningEngine] Max thoughts ({Max}) reached, forcing conclusion", MaxThoughts);
            var forced = new ThoughtStep
            {
                Type = ThoughtType.Conclusion,
                Content = "Reasoning limit reached. Proceeding with current understanding.",
                Conclusion = context.Thoughts.LastOrDefault()?.Content ?? context.Goal
            };
            context.AddThought(forced);
            context.CurrentConclusion = forced.Conclusion;
            context.IsComplete = true;
            return forced;
        }

        var trace = context.GetReasoningTrace();
        var userMessage = $"Goal: {context.Goal}\n\nReasoning so far:\n{trace}\n\nContinue your reasoning. If you have reached a conclusion about the plan, use type \"Conclusion\".";

        var thought = await GenerateThoughtAsync(userMessage, cancellationToken);
        context.AddThought(thought);

        if (thought.Type == ThoughtType.Conclusion)
        {
            context.CurrentConclusion = thought.Conclusion ?? thought.Content;
            context.IsComplete = true;
        }

        return thought;
    }

    public async Task<Plan> GeneratePlanFromReasoningAsync(ReasoningContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[ReasoningEngine] Generating plan from reasoning for goal: {Goal}", context.Goal);

        var trace = context.GetReasoningTrace();
        var userMessage = $"Based on the following reasoning, create a concrete execution plan.\n\nGoal: {context.Goal}\n\nReasoning:\n{trace}\n\nAvailable tools: {string.Join(", ", context.AvailableTools)}\n\nRespond with a JSON plan:\n{{\"goal\": \"...\", \"steps\": [{{\"action\": \"...\", \"description\": \"...\", \"tool\": \"tool_name_or_null\", \"parameters\": {{}}}}]}}";

        var response = await _aiService.ChatAsync(userMessage, cancellationToken: cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("[ReasoningEngine] AI failed to generate plan, creating simple plan");
            return CreateSimplePlan(context);
        }

        return ParsePlanFromJson(context.Goal, response.Content);
    }

    private async Task<string> BuildMemoryBlockAsync(string goal, CancellationToken cancellationToken)
    {
        if (_memory is null)
            return string.Empty;

        try
        {
            var memoryContext = await _memory.BuildContextAsync(goal, project: null, limitPerScope: 5, cancellationToken);
            if (memoryContext.TotalCount > 0)
                return $"\n\nRelevant memories:\n{memoryContext.Render()}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ReasoningEngine] Failed to build memory context");
        }

        return string.Empty;
    }

    private async Task<ThoughtStep> GenerateThoughtAsync(string userMessage, CancellationToken cancellationToken)
    {
        var response = await _aiService.ChatAsync(userMessage, cancellationToken: cancellationToken);

        if (!response.Success)
        {
            return new ThoughtStep
            {
                Type = ThoughtType.Observation,
                Content = $"Unable to process: {response.ErrorMessage}",
                Conclusion = null
            };
        }

        return ParseThoughtFromJson(response.Content);
    }

    private ThoughtStep ParseThoughtFromJson(string responseContent)
    {
        try
        {
            var cleaned = ExtractJsonFromResponse(responseContent);
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var typeStr = root.TryGetProperty("type", out var type) ? type.GetString() : "Analysis";
            var content = root.TryGetProperty("content", out var contentProp) ? contentProp.GetString() ?? "" : "";
            var conclusion = root.TryGetProperty("conclusion", out var concl) && concl.ValueKind != JsonValueKind.Null ? concl.GetString() : null;

            var thoughtType = typeStr?.ToLowerInvariant() switch
            {
                "decision" => ThoughtType.Decision,
                "observation" => ThoughtType.Observation,
                "planning" => ThoughtType.Planning,
                "reflection" => ThoughtType.Reflection,
                "conclusion" => ThoughtType.Conclusion,
                _ => ThoughtType.Analysis
            };

            return new ThoughtStep { Type = thoughtType, Content = content, Conclusion = conclusion };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ReasoningEngine] Failed to parse thought JSON, using raw content");
            return new ThoughtStep
            {
                Type = ThoughtType.Observation,
                Content = responseContent.Length > 500 ? responseContent[..500] : responseContent
            };
        }
    }

    private Plan ParsePlanFromJson(string goal, string responseContent)
    {
        try
        {
            var cleaned = ExtractJsonFromResponse(responseContent);
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var plan = new Plan { Goal = goal, Status = PlanStatus.Created };

            if (root.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var stepElement in stepsElement.EnumerateArray())
                {
                    var step = new PlanStep
                    {
                        Index = index++,
                        Action = stepElement.TryGetProperty("action", out var action) ? action.GetString() ?? "unknown" : "unknown",
                        Description = stepElement.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                        ToolName = stepElement.TryGetProperty("tool", out var tool) && tool.ValueKind != JsonValueKind.Null ? tool.GetString() : null
                    };

                    if (stepElement.TryGetProperty("parameters", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in paramsElement.EnumerateObject())
                        {
                            step.Parameters[prop.Name] = prop.Value.ToString();
                        }
                    }

                    plan.Steps.Add(step);
                }
            }

            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ReasoningEngine] Failed to parse plan JSON");
            return CreateSimplePlanFromGoal(goal);
        }
    }

    private Plan CreateSimplePlan(ReasoningContext context)
    {
        var plan = new Plan { Goal = context.Goal, Status = PlanStatus.Created };
        plan.Steps.Add(new PlanStep
        {
            Index = 0,
            Action = "Execute analyzed goal",
            Description = context.CurrentConclusion ?? context.Goal,
            ToolName = context.AvailableTools.FirstOrDefault()
        });
        return plan;
    }

    private static Plan CreateSimplePlanFromGoal(string goal)
    {
        return new Plan
        {
            Goal = goal,
            Status = PlanStatus.Created,
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Action = "Execute goal", Description = goal }
            }
        };
    }

    private static string ExtractJsonFromResponse(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start >= 0 && end > start)
            return response.Substring(start, end - start + 1);
        return response;
    }
}
