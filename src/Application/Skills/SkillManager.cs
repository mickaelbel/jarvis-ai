using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Skills;

/// <summary>
/// Système de skills auto-améliorants : l'agent peut créer, modifier, et
/// utiliser des skills (compétences) qui s'améliorent avec l'usage. Les skills
/// sont persistés sur disque et peuvent être partagés.
/// </summary>
public sealed class SkillManager
{
    private readonly ILogger<SkillManager> _logger;
    private static readonly string SkillsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "skills");
    private readonly Dictionary<string, Skill> _skills = new();

    public SkillManager(ILogger<SkillManager> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(SkillsDir);
        LoadAll();
    }

    /// <summary>
    /// Crée un nouveau skill à partir d'une tâche complexe réussie.
    /// </summary>
    public Skill CreateSkill(string name, string description, string prompt, string category = "general")
    {
        var skill = new Skill
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name,
            Description = description,
            Prompt = prompt,
            Category = category,
            CreatedAt = DateTime.UtcNow,
            UsageCount = 0,
            Version = 1,
            Status = SkillStatus.Active
        };
        _skills[skill.Id] = skill;
        Persist(skill);
        _logger.LogInformation("[SkillManager] Skill créé: {Name} ({Id})", name, skill.Id);
        return skill;
    }

    /// <summary>
    /// Enregistre l'usage d'un skill (incrémente le compteur, met à jour la date).
    /// </summary>
    public void RecordUsage(string skillId)
    {
        if (_skills.TryGetValue(skillId, out var skill))
        {
            skill.UsageCount++;
            skill.LastUsedAt = DateTime.UtcNow;
            Persist(skill);
        }
    }

    /// <summary>
    /// Améliore un skill (version bump + mise à jour du prompt).
    /// </summary>
    public Skill? ImproveSkill(string skillId, string newPrompt, string? reason = null)
    {
        if (!_skills.TryGetValue(skillId, out var skill)) return null;
        skill.Prompt = newPrompt;
        skill.Version++;
        skill.LastImprovedAt = DateTime.UtcNow;
        skill.ImprovementReason = reason;
        Persist(skill);
        _logger.LogInformation("[SkillManager] Skill amélioré: {Name} v{Version}", skill.Name, skill.Version);
        return skill;
    }

    /// <summary>
    /// Archive un skill (ne le supprime pas).
    /// </summary>
    public bool ArchiveSkill(string skillId)
    {
        if (!_skills.TryGetValue(skillId, out var skill)) return false;
        skill.Status = SkillStatus.Archived;
        skill.ArchivedAt = DateTime.UtcNow;
        Persist(skill);
        return true;
    }

    public Skill? GetSkill(string skillId) => _skills.TryGetValue(skillId, out var s) ? s : null;
    public Skill? GetSkillByName(string name) => _skills.Values.FirstOrDefault(s => s.Name == name, null);
    public IReadOnlyList<Skill> GetAllSkills() => _skills.Values.ToList().AsReadOnly();
    public IReadOnlyList<Skill> GetActiveSkills() => _skills.Values.Where(s => s.Status == SkillStatus.Active).ToList().AsReadOnly();
    public IReadOnlyList<Skill> SearchSkills(string query) =>
        _skills.Values.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   s.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList().AsReadOnly();

    private void Persist(Skill skill)
    {
        var path = GetPath(skill.Id);
        var json = JsonSerializer.Serialize(skill, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private void LoadAll()
    {
        try
        {
            foreach (var file in Directory.GetFiles(SkillsDir, "*.json"))
            {
                var json = File.ReadAllText(file);
                var skill = JsonSerializer.Deserialize<Skill>(json);
                if (skill is not null) _skills[skill.Id] = skill;
            }
            _logger.LogInformation("[SkillManager] {Count} skills chargés", _skills.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SkillManager] Erreur chargement skills");
        }
    }

    private static string GetPath(string skillId) => Path.Combine(SkillsDir, $"{skillId}.json");
}

public sealed class Skill
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Category { get; set; } = "general";
    public SkillStatus Status { get; set; } = SkillStatus.Active;
    public int Version { get; set; } = 1;
    public int UsageCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? LastImprovedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public string? ImprovementReason { get; set; }
}

public enum SkillStatus { Active, Archived, Deprecated }
