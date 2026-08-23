using JarvisAI.Application.Agents;
using JarvisAI.Application.Planning;

namespace JarvisAI.Web.Services;

public sealed class AgentRunStepDto
{
    public int Index { get; set; }
    public RunStepKind Kind { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public bool? Success { get; set; }
    public double? DurationMs { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class AgentPlanStepDto
{
    public int Index { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public PlanStepStatus Status { get; set; }
}

public sealed class AgentRunDto
{
    public Guid RunId { get; set; }
    public string Goal { get; set; } = string.Empty;
    public RunStatus Status { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public double DurationSeconds { get; set; }
    public int Iterations { get; set; }
    public int Retries { get; set; }
    public int ToolsExecuted { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public string? FinalResponse { get; set; }
    public string? Error { get; set; }
    public string? Reason { get; set; }
    public List<AgentRunStepDto> Steps { get; set; } = new();
    public List<AgentPlanStepDto>? PlanSteps { get; set; }
    public List<string> ReasoningTrace { get; set; } = new();
    public List<string> MemoryNotes { get; set; } = new();
    public List<string> Plugins { get; set; } = new();

    public static AgentRunDto From(RunRecord run)
    {
        var dto = new AgentRunDto
        {
            RunId = run.RunId,
            Goal = run.Goal,
            Status = run.Status,
            Model = run.Model,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            DurationSeconds = run.Duration.TotalSeconds,
            Iterations = run.Iterations,
            Retries = run.Retries,
            ToolsExecuted = run.ToolsExecuted,
            PromptTokens = run.PromptTokens,
            CompletionTokens = run.CompletionTokens,
            FinalResponse = run.FinalResponse,
            Error = run.Error,
            Reason = run.Reason,
            Steps = run.Steps.Select(s => new AgentRunStepDto
            {
                Index = s.Index,
                Kind = s.Kind,
                Content = s.Content,
                ToolName = s.ToolName,
                Success = s.Success,
                DurationMs = s.Duration?.TotalMilliseconds,
                OccurredAt = s.OccurredAt
            }).ToList(),
            ReasoningTrace = run.ReasoningTrace.ToList(),
            MemoryNotes = run.MemoryNotes.ToList(),
            Plugins = run.Plugins.ToList(),
            PlanSteps = run.Plan?.Steps.Select(p => new AgentPlanStepDto
            {
                Index = p.Index,
                Action = p.Action,
                Description = p.Description,
                ToolName = p.ToolName,
                Status = p.Status
            }).ToList()
        };

        return dto;
    }
}
