using JarvisAI.Application.Agents;
using JarvisAI.Application.Personality;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Personnalités de Jarvis : neutre, majordome sarcastique, concis.
/// Changable à la voix (« passe en mode majordome »).
/// </summary>
public sealed class PersonalityTool : ITool
{
    private readonly PersonalityStore _store;

    public string Name => "personality";
    public string Description =>
        "Change la personnalité de Jarvis. Actions : list, set (paramètre style = neutre | majordome | concis), current. " +
        "Pour « passe en mode majordome », « sois plus concis ».";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "list | set | current", typeof(string), required: true),
        new ToolParameter("style", "neutre | majordome | concis", typeof(string))
    };

    public PersonalityTool(PersonalityStore store) => _store = store;

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("style", out var style);

        return Task.FromResult((action?.ToLowerInvariant()) switch
        {
            "list" => ToolResult.Succeeded(
                "Personnalités disponibles :\n" +
                string.Join("\n", PersonalityStore.ListStyles().Select(s => $"  - {s.Key} ({s.Label})")) +
                $"\n\nActuelle : {_store.Current}"),
            "current" => ToolResult.Succeeded($"Personnalité actuelle : {_store.Current}"),
            "set" => string.IsNullOrWhiteSpace(style) || !_store.TrySet(style.Trim())
                ? ToolResult.Failed($"Style inconnu : « {style} ». Valides : neutre, majordome, concis")
                : ToolResult.Succeeded($"Personnalité changée : « {style.Trim()} » active. ACTION TERMINÉE."),
            _ => ToolResult.Failed($"Action inconnue : {action}. Valides : list, set, current")
        });
    }
}
