using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Skills;

/// <summary>
/// Outil skills : l'agent peut créer, lister, utiliser, et améliorer des skills.
/// </summary>
public sealed class SkillTool : ITool
{
    private readonly SkillManager _manager;
    private readonly ILogger<SkillTool> _logger;

    public string Name => "skill";
    public string Description =>
        "Gérer les skills (compétences). Créer un skill après une tâche complexe réusissie, " +
        "lister les skills disponibles, utiliser un skill, l'améliorer, ou l'archiver.";
    public string Category => "meta";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Action: create, list, use, improve, archive, search", typeof(string), required: true),
        new ToolParameter("name", "Nom du skill (pour create)", typeof(string), required: false),
        new ToolParameter("description", "Description du skill (pour create)", typeof(string), required: false),
        new ToolParameter("prompt", "Prompt/instructions du skill (pour create/improve)", typeof(string), required: false),
        new ToolParameter("category", "Catégorie du skill (pour create)", typeof(string), required: false),
        new ToolParameter("skill_id", "ID du skill (pour use/improve/archive)", typeof(string), required: false),
        new ToolParameter("query", "Recherche (pour search)", typeof(string), required: false),
    };

    public SkillTool(SkillManager manager, ILogger<SkillTool> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        if (string.IsNullOrWhiteSpace(action))
            return Task.FromResult(ToolResult.Failed("Paramètre 'action' requis."));

        return action.ToLowerInvariant() switch
        {
            "create" => Create(parameters),
            "list" => List(),
            "use" => Use(parameters),
            "improve" => Improve(parameters),
            "archive" => Archive(parameters),
            "search" => Search(parameters),
            _ => Task.FromResult(ToolResult.Failed($"Action inconnue: {action}"))
        };
    }

    private Task<ToolResult> Create(IReadOnlyDictionary<string, string> p)
    {
        p.TryGetValue("name", out var name);
        p.TryGetValue("description", out var desc);
        p.TryGetValue("prompt", out var prompt);
        p.TryGetValue("category", out var cat);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prompt))
            return Task.FromResult(ToolResult.Failed("name et prompt requis pour create."));

        var skill = _manager.CreateSkill(name, desc ?? "", prompt, cat ?? "general");
        return Task.FromResult(ToolResult.Succeeded($"Skill '{name}' créé ({skill.Id})."));
    }

    private Task<ToolResult> List()
    {
        var skills = _manager.GetActiveSkills();
        if (skills.Count == 0)
            return Task.FromResult(ToolResult.Succeeded("Aucun skill actif."));

        var lines = skills.Select(s => $"- {s.Name} ({s.Id}) v{s.Version} — {s.Description} (utilisé {s.UsageCount}x)");
        return Task.FromResult(ToolResult.Succeeded(string.Join("\n", lines)));
    }

    private Task<ToolResult> Use(IReadOnlyDictionary<string, string> p)
    {
        p.TryGetValue("skill_id", out var id);
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(ToolResult.Failed("skill_id requis."));
        var skill = _manager.GetSkill(id) ?? _manager.GetSkillByName(id);
        if (skill is null) return Task.FromResult(ToolResult.Succeeded("Skill introuvable."));
        _manager.RecordUsage(skill.Id);
        return Task.FromResult(ToolResult.Succeeded($"[Skill: {skill.Name}]\n{skill.Prompt}"));
    }

    private Task<ToolResult> Improve(IReadOnlyDictionary<string, string> p)
    {
        p.TryGetValue("skill_id", out var id);
        p.TryGetValue("prompt", out var newPrompt);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(newPrompt))
            return Task.FromResult(ToolResult.Failed("skill_id et prompt requis."));
        var improved = _manager.ImproveSkill(id, newPrompt);
        return Task.FromResult(improved is not null
            ? ToolResult.Succeeded($"Skill '{improved.Name}' amélioré → v{improved.Version}")
            : ToolResult.Failed("Skill introuvable."));
    }

    private Task<ToolResult> Archive(IReadOnlyDictionary<string, string> p)
    {
        p.TryGetValue("skill_id", out var id);
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(ToolResult.Failed("skill_id requis."));
        var archived = _manager.ArchiveSkill(id);
        return Task.FromResult(archived ? ToolResult.Succeeded("Skill archivé.") : ToolResult.Failed("Skill introuvable."));
    }

    private Task<ToolResult> Search(IReadOnlyDictionary<string, string> p)
    {
        p.TryGetValue("query", out var query);
        if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(ToolResult.Failed("query requis."));
        var results = _manager.SearchSkills(query);
        if (results.Count == 0) return Task.FromResult(ToolResult.Succeeded("Aucun skill trouvé."));
        var lines = results.Select(s => $"- {s.Name} ({s.Id}): {s.Description}");
        return Task.FromResult(ToolResult.Succeeded(string.Join("\n", lines)));
    }
}
