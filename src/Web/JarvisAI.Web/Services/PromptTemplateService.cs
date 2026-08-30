using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IPromptTemplateService
{
    IReadOnlyList<PromptTemplate> GetTemplates(string? category = null);
    PromptTemplate? GetTemplate(string templateId);
    string CreateTemplate(string name, string content, string category, bool isSystem = false);
    void UpdateTemplate(string templateId, string content);
    void DeleteTemplate(string templateId);
    string RenderTemplate(string templateId, Dictionary<string, string> variables);
    IReadOnlyList<string> GetCategories();
}

public sealed class PromptTemplateService : IPromptTemplateService
{
    private readonly ILogger<PromptTemplateService> _logger;
    private readonly string _storagePath;
    private readonly List<PromptTemplate> _templates = new();

    public PromptTemplateService(ILogger<PromptTemplateService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "prompt_templates.json");
        Load();
        InitializeDefaults();
    }

    public IReadOnlyList<PromptTemplate> GetTemplates(string? category = null)
    {
        if (category is null) return _templates;
        return _templates.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public PromptTemplate? GetTemplate(string templateId)
        => _templates.FirstOrDefault(t => t.Id == templateId);

    public string CreateTemplate(string name, string content, string category, bool isSystem = false)
    {
        var template = new PromptTemplate
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Content = content,
            Category = category,
            IsSystem = isSystem,
            Variables = ExtractVariables(content),
            CreatedAt = DateTime.UtcNow
        };

        _templates.Add(template);
        Save();
        return template.Id;
    }

    public void UpdateTemplate(string templateId, string content)
    {
        var template = _templates.FirstOrDefault(t => t.Id == templateId);
        if (template is not null)
        {
            template.Content = content;
            template.Variables = ExtractVariables(content);
            template.LastModified = DateTime.UtcNow;
            Save();
        }
    }

    public void DeleteTemplate(string templateId)
    {
        _templates.RemoveAll(t => t.Id == templateId);
        Save();
    }

    public string RenderTemplate(string templateId, Dictionary<string, string> variables)
    {
        var template = GetTemplate(templateId);
        if (template is null) return "";

        var result = template.Content;
        foreach (var kv in variables)
        {
            result = result.Replace($"{{{{{kv.Key}}}}}", kv.Value);
        }
        return result;
    }

    public IReadOnlyList<string> GetCategories()
        => _templates.Select(t => t.Category).Distinct().OrderBy(c => c).ToList();

    private List<string> ExtractVariables(string content)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(content, @"\{\{(\w+)\}\}");
        return matches.Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    private void InitializeDefaults()
    {
        if (_templates.Count > 0) return;

        var defaults = new List<PromptTemplate>
        {
            new() { Name = "Résumé de code", Content = "Résume le following code en expliquant sa fonctionnalité principale et son architecture:\n\n```{{language}}\n{{code}}\n```", Category = "Code", IsSystem = true },
            new() { Name = "Revue de code", Content = "Analyse ce code pour les bugs, failles de sécurité et optimisations potentielles:\n\n```{{language}}\n{{code}}\n```", Category = "Code", IsSystem = true },
            new() { Name = "Génération de tests", Content = "Génère des tests unitaires pour ce code avec couverture des cas limites:\n\n```{{language}}\n{{code}}\n```", Category = "Code", IsSystem = true },
            new() { Name = "Traduction", Content = "Traduis le following texte en {{target_language}}:\n\n{{text}}", Category = "Général", IsSystem = true },
            new() { Name = "Résumé de texte", Content = "Résume le following texte en 3-5 bullet points:\n\n{{text}}", Category = "Général", IsSystem = true },
            new() { Name = "Explication", Content = "Explique {{topic}} de manière simple et concrète avec des exemples.", Category = "Général", IsSystem = true },
            new() { Name = "Email professionnel", Content = "Rédige un email professionnel à {{recipient}} concernant {{subject}}. Ton: {{tone}}.", Category = "Communication", IsSystem = true },
            new() { Name = "Message Slack", Content = "Rédige un message Slack court et professionnel pour l'équipe {{team}}:\n\n{{context}}", Category = "Communication", IsSystem = true },
            new() { Name = "Documentation API", Content = "Génère la documentation pour cette endpoint API:\n\n{{endpoint}}\n\nMethod: {{method}}\nBody: {{body}}", Category = "API", IsSystem = true },
            new() { Name = "SQL Query", Content = "Écris une requête SQL pour: {{requirement}}\n\nSchéma:\n{{schema}}", Category = "Database", IsSystem = true },
        };

        foreach (var template in defaults)
        {
            template.Id = Guid.NewGuid().ToString("N")[..8];
            template.Variables = ExtractVariables(template.Content);
            template.CreatedAt = DateTime.UtcNow;
            _templates.Add(template);
        }

        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<PromptTemplate>>(json);
                if (loaded is not null) _templates.AddRange(loaded);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_templates, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class PromptTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsSystem { get; set; }
    public List<string> Variables { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModified { get; set; }
}
