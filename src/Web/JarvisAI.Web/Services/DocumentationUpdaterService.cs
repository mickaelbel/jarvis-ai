using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Web.Services;

public interface IDocumentationUpdaterService
{
    DocumentationDiff DetectChanges(string oldCode, string newCode);
    string GenerateChangelog(List<DocumentationChange> changes);
    IReadOnlyList<string> GetAffectedDocs(string filePath, List<string> allDocPaths);
    string UpdateReadme(string readmePath, List<DocumentationChange> changes);
}

public sealed class DocumentationUpdaterService : IDocumentationUpdaterService
{
    private readonly ILogger<DocumentationUpdaterService> _logger;

    public DocumentationUpdaterService(ILogger<DocumentationUpdaterService> logger)
    {
        _logger = logger;
    }

    public DocumentationDiff DetectChanges(string oldCode, string newCode)
    {
        var oldMethods = ExtractMethods(oldCode);
        var newMethods = ExtractMethods(newCode);

        var added = newMethods.Where(n => !oldMethods.Any(o => o.Name == n.Name)).ToList();
        var removed = oldMethods.Where(o => !newMethods.Any(n => n.Name == o.Name)).ToList();
        var modified = newMethods.Where(n =>
        {
            var old = oldMethods.FirstOrDefault(o => o.Name == n.Name);
            return old is not null && old.Signature != n.Signature;
        }).ToList();

        var changes = new List<DocumentationChange>();

        foreach (var method in added)
            changes.Add(new DocumentationChange { Type = ChangeType.Added, Name = method.Name, Description = $"Nouvelle méthode: {method.Signature}" });

        foreach (var method in removed)
            changes.Add(new DocumentationChange { Type = ChangeType.Removed, Name = method.Name, Description = $"Méthode supprimée: {method.Name}" });

        foreach (var method in modified)
            changes.Add(new DocumentationChange { Type = ChangeType.Modified, Name = method.Name, Description = $"Signature modifiée: {method.Signature}" });

        return new DocumentationDiff
        {
            Changes = changes,
            BreakingChanges = changes.Where(c => c.Type == ChangeType.Removed || c.IsBreaking).ToList(),
            Summary = GenerateSummary(changes)
        };
    }

    public string GenerateChangelog(List<DocumentationChange> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Changelog");
        sb.AppendLine();
        sb.AppendLine($"Généré le {DateTime.UtcNow:dd/MM/yyyy HH:mm}");
        sb.AppendLine();

        var breaking = changes.Where(c => c.IsBreaking).ToList();
        var added = changes.Where(c => c.Type == ChangeType.Added).ToList();
        var modified = changes.Where(c => c.Type == ChangeType.Modified).ToList();
        var removed = changes.Where(c => c.Type == ChangeType.Removed).ToList();

        if (breaking.Any())
        {
            sb.AppendLine("## ⚠️ Breaking Changes");
            foreach (var change in breaking)
                sb.AppendLine($"- {change.Description}");
            sb.AppendLine();
        }

        if (added.Any())
        {
            sb.AppendLine("## ✨ Nouveautés");
            foreach (var change in added)
                sb.AppendLine($"- {change.Description}");
            sb.AppendLine();
        }

        if (modified.Any())
        {
            sb.AppendLine("## 🔄 Modifications");
            foreach (var change in modified)
                sb.AppendLine($"- {change.Description}");
            sb.AppendLine();
        }

        if (removed.Any())
        {
            sb.AppendLine("## ❌ Suppressions");
            foreach (var change in removed)
                sb.AppendLine($"- {change.Description}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public IReadOnlyList<string> GetAffectedDocs(string filePath, List<string> allDocPaths)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return allDocPaths.Where(doc =>
        {
            var content = File.ReadAllText(doc);
            return content.Contains(fileName, StringComparison.OrdinalIgnoreCase);
        }).ToList();
    }

    public string UpdateReadme(string readmePath, List<DocumentationChange> changes)
    {
        if (!File.Exists(readmePath))
            return "README non trouvé";

        var content = File.ReadAllText(readmePath);
        var sb = new StringBuilder();

        sb.AppendLine(content);
        sb.AppendLine();
        sb.AppendLine("## Dernières modifications");
        sb.AppendLine();

        foreach (var change in changes.Take(10))
        {
            var icon = change.Type switch
            {
                ChangeType.Added => "✨",
                ChangeType.Removed => "❌",
                ChangeType.Modified => "🔄",
                _ => "📝"
            };
            sb.AppendLine($"- {icon} {change.Description}");
        }

        return sb.ToString();
    }

    private List<MethodInfo> ExtractMethods(string code)
    {
        var methods = new List<MethodInfo>();
        var matches = Regex.Matches(code,
            @"(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?(\w+(?:<[^>]+>)?)\s+(\w+)\s*\(([^)]*)\)");

        foreach (Match match in matches)
        {
            methods.Add(new MethodInfo
            {
                Name = match.Groups[2].Value,
                ReturnType = match.Groups[1].Value,
                Signature = match.Value
            });
        }

        return methods;
    }

    private bool HasBreakingChanges(MethodInfo oldMethod, MethodInfo newMethod)
    {
        // Breaking if: return type changed, parameters removed, parameters reordered
        if (oldMethod.ReturnType != newMethod.ReturnType) return true;

        var oldParams = oldMethod.Signature.Split('(').ElementAtOrDefault(1)?.Split(')')?.ElementAtOrDefault(0)?.Split(',') ?? Array.Empty<string>();
        var newParams = newMethod.Signature.Split('(').ElementAtOrDefault(1)?.Split(')')?.ElementAtOrDefault(0)?.Split(',') ?? Array.Empty<string>();

        return oldParams.Length > newParams.Length;
    }

    private string GenerateSummary(List<DocumentationChange> changes)
    {
        if (!changes.Any()) return "Aucun changement détecté";

        var added = changes.Count(c => c.Type == ChangeType.Added);
        var removed = changes.Count(c => c.Type == ChangeType.Removed);
        var modified = changes.Count(c => c.Type == ChangeType.Modified);

        return $"{added} ajouté(s), {removed} supprimé(s), {modified} modifié(s)";
    }
}

public sealed class DocumentationDiff
{
    public List<DocumentationChange> Changes { get; set; } = new();
    public List<DocumentationChange> BreakingChanges { get; set; } = new();
    public string Summary { get; set; } = "";
}

public sealed class DocumentationChange
{
    public ChangeType Type { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsBreaking { get; set; }
}

public enum ChangeType { Added, Removed, Modified }

internal class MethodInfo
{
    public string Name { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public string Signature { get; set; } = "";
}
