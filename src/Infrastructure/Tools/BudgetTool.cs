using JarvisAI.Application.Agents;
using JarvisAI.Application.Budget;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>Consultation des dépenses IA et plafonds (lecture seule, sûr).</summary>
public sealed class BudgetTool : ITool
{
    private readonly IBudgetTracker _tracker;
    public string Name => "budget";
    public string Description => "Dépenses IA : combien j'ai dépensé aujourd'hui et ce mois-ci, par rapport aux plafonds configurés. Action : status.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Action : status", typeof(string), required: true)
    };

    public BudgetTool(IBudgetTracker tracker) => _tracker = tracker;

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        => Task.FromResult(ToolResult.Succeeded(_tracker.FormatSummary()));
}
