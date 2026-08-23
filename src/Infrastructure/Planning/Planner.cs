using System.Text.Json;
using JarvisAI.Application.AI;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Planning;

public sealed class Planner : IPlanner
{
    private readonly AIService _aiService;
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<Planner> _logger;
    private readonly IMemoryService? _memory;

    private const string PlanSystemPrompt = @"You are a planning assistant. Given a goal and a list of available tools, create a step-by-step plan.

Respond with ONLY a JSON object in this exact format:
{
  ""goal"": ""<the goal>"",
  ""steps"": [
    {
      ""action"": ""<short action name>"",
      ""description"": ""<what this step does>"",
      ""tool"": ""<tool name or null if not a tool step>"",
      ""parameters"": { ""key"": ""value"" }
    }
  ]
}

Rules:
- Each step should be atomic and executable
- Use only tools from the provided list
- Steps should be in logical execution order
- Include all necessary parameters for tool calls
- If no tool is needed, set ""tool"": null and describe the action";

    public Planner(AIService aiService, IToolRegistry toolRegistry, ILogger<Planner> logger, IMemoryService? memory = null)
    {
        _aiService = aiService;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _memory = memory;
    }

    public async Task<Plan> CreatePlanAsync(string goal, CancellationToken cancellationToken = default)
    {
        var toolNames = _toolRegistry.GetAll().Select(t => t.Name).ToList();
        return await CreatePlanWithContextAsync(goal, toolNames, cancellationToken);
    }

    public async Task<Plan> CreatePlanWithContextAsync(string goal, IReadOnlyList<string> context, CancellationToken cancellationToken = default)
        => await CreatePlanWithInstructionsAsync(goal, string.Empty, context, cancellationToken);

    public async Task<Plan> CreatePlanWithInstructionsAsync(string goal, string instructions, IReadOnlyList<string> context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[Planner] Creating plan for goal: {Goal}", goal);

        var toolList = string.Join(", ", context);
        var memoryBlock = await BuildMemoryBlockAsync(goal, cancellationToken);
        var instructionBlock = string.IsNullOrWhiteSpace(instructions) ? string.Empty : $"\n\nStrategy instructions:\n{instructions}";
        var userMessage = $"Goal: {goal}\n\nAvailable tools: {toolList}{memoryBlock}{instructionBlock}";

        var response = await _aiService.ChatAsync(userMessage, cancellationToken: cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("[Planner] AI failed to generate plan: {Error}", response.ErrorMessage);
            return CreateFallbackPlan(goal);
        }

        return ParsePlanResponse(goal, response.Content);
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
            _logger.LogWarning(ex, "[Planner] Failed to build memory context");
        }

        return string.Empty;
    }

    private Plan ParsePlanResponse(string goal, string responseContent)
    {
        try
        {
            var cleaned = ExtractJsonFromResponse(responseContent);
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var plan = new Plan
            {
                Goal = goal,
                Status = PlanStatus.Created
            };

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

            _logger.LogInformation("[Planner] Plan created with {StepCount} steps", plan.Steps.Count);
            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Planner] Failed to parse plan response, creating fallback plan");
            return CreateFallbackPlan(goal);
        }
    }

    private static string ExtractJsonFromResponse(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start >= 0 && end > start)
            return response.Substring(start, end - start + 1);
        return response;
    }

    private Plan CreateFallbackPlan(string goal)
    {
        _logger.LogWarning("[Planner] Creating fallback plan for: {Goal}", goal);
        return new Plan
        {
            Goal = goal,
            Status = PlanStatus.Created,
            Steps = new List<PlanStep>
            {
                new()
                {
                    Index = 0,
                    Action = "Execute goal directly",
                    Description = goal,
                    ToolName = null
                }
            }
        };
    }
}
