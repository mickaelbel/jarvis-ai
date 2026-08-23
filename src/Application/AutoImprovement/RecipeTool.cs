using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using System.Text;

namespace JarvisAI.Application.AutoImprovement;

/// <summary>
/// Outil auto-créé composé d'étapes qui appellent des outils déjà approuvés.
/// Chaque étape repasse par IToolExecutor (et donc la sécurité existante :
/// confirmations, whitelist, timeouts, événements).
/// </summary>
public sealed class RecipeTool : ITool
{
    private readonly IToolExecutor _executor;

    public AutoToolSpec Spec { get; }
    public string Name => Spec.Name;
    public string Description => Spec.Description;
    public string Category => Spec.Category;
    public SecurityRiskLevel RiskLevel => ParseRisk(Spec.RiskLevel);

    public IReadOnlyList<ToolParameter> Parameters =>
        Spec.Parameters.Select(p => new ToolParameter(p.Name, p.Description, ResolveType(p.Type), p.Required)).ToList();

    public RecipeTool(AutoToolSpec spec, IToolExecutor executor)
    {
        Spec = spec;
        _executor = executor;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Spec.Name };
        var output = new StringBuilder();

        foreach (var step in Spec.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!visited.Add(step.ToolName))
                return ToolResult.Failed($"Recipe cycle detected at tool '{step.ToolName}'.");

            var stepArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in step.Arguments)
                stepArgs[kv.Key] = ResolveTemplate(kv.Value, parameters);

            var child = new AgentContext(
                context.CommandText,
                context.Source,
                new Dictionary<string, object>(context.Metadata) { ["arguments"] = stepArgs });

            var result = await _executor.ExecuteAsync(step.ToolName, child, cancellationToken);
            if (!result.Success)
                return ToolResult.Failed($"Step '{step.ToolName}' failed: {result.ErrorMessage}");

            output.AppendLine($"## {step.ToolName}");
            output.AppendLine(result.Output);
        }

        return ToolResult.Succeeded(output.ToString().TrimEnd());
    }

    private static string ResolveTemplate(string template, IReadOnlyDictionary<string, string> parameters)
    {
        if (!template.Contains('{') && !template.Contains('$'))
            return template;

        var sb = new StringBuilder(template);
        foreach (var p in parameters)
        {
            sb.Replace("{" + p.Key + "}", p.Value);
            sb.Replace("$" + p.Key, p.Value);
        }
        return sb.ToString();
    }

    private static Type ResolveType(string type) => type.ToLowerInvariant() switch
    {
        "number" or "double" or "int" => typeof(double),
        "boolean" or "bool" => typeof(bool),
        _ => typeof(string)
    };

    public static SecurityRiskLevel ParseRisk(string risk) => risk.ToLowerInvariant() switch
    {
        "high" => SecurityRiskLevel.High,
        "medium" => SecurityRiskLevel.Medium,
        _ => SecurityRiskLevel.Low
    };
}
