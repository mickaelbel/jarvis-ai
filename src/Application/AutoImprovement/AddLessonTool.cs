using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Application.AutoImprovement;

/// <summary>
/// Mémoire d'auto-amélioration : Jarvis consigne ce qu'il a appris (lacunes,
/// échecs, règles durables). Ces leçons sont réinjectées dans son contexte.
/// </summary>
public sealed class AddLessonTool : ITool
{
    private readonly ISelfImprovementManager _manager;

    public string Name => "add_lesson";
    public string Description =>
        "Save a durable lesson you learned: a capability you lack, a mistake to avoid, " +
        "a preference the user expressed, or a rule that should guide your future behavior. " +
        "Lessons persist across sessions and are shown to you as context.";
    public string Category => "self-improvement";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("lesson", "The lesson, fact or rule to remember (max 500 chars).", typeof(string), true)
    };

    public AddLessonTool(ISelfImprovementManager manager) => _manager = manager;

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("lesson", out var lesson);
        _manager.AddLesson(lesson ?? "");
        return Task.FromResult(ToolResult.Succeeded("Lesson saved."));
    }
}
