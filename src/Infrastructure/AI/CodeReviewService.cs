using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.AI;

public interface ICodeReviewService
{
    Task<CodeReviewResult> ReviewCodeAsync(string code, string language, CancellationToken ct = default);
    Task<CodeReviewResult> ReviewFileAsync(string filePath, CancellationToken ct = default);
    IReadOnlyList<CodeReviewRule> GetRules(string? language = null);
}

public sealed class CodeReviewService : ICodeReviewService
{
    private readonly ILogger<CodeReviewService> _logger;
    private readonly List<CodeReviewRule> _rules = new();

    public CodeReviewService(ILogger<CodeReviewService> logger)
    {
        _logger = logger;
        InitializeRules();
    }

    public async Task<CodeReviewResult> ReviewCodeAsync(string code, string language, CancellationToken ct = default)
    {
        var issues = new List<CodeIssue>();
        var lines = code.Split('\n');
        var lang = language.ToLowerInvariant();

        foreach (var rule in _rules.Where(r => r.Languages.Contains("*") || r.Languages.Contains(lang)))
        {
            foreach (var line in lines)
            {
                if (rule.Pattern.IsMatch(line))
                {
                    issues.Add(new CodeIssue
                    {
                        Severity = rule.Severity,
                        Rule = rule.Name,
                        Message = rule.Message,
                        Suggestion = rule.Suggestion
                    });
                }
            }
        }

        // C# specific checks
        if (lang == "csharp" || lang == "cs")
        {
            issues.AddRange(ReviewCSharp(code, lines));
        }

        var score = Math.Max(0, 100 - (issues.Count(i => i.Severity == IssueSeverity.Error) * 10) -
            (issues.Count(i => i.Severity == IssueSeverity.Warning) * 3) -
            (issues.Count(i => i.Severity == IssueSeverity.Info) * 1));

        var result = new CodeReviewResult
        {
            Language = language,
            TotalLines = lines.Length,
            Issues = issues,
            QualityScore = score,
            Summary = GenerateSummary(issues, score),
            ReviewedAt = DateTime.UtcNow
        };

        _logger.LogInformation("[CodeReview] {Lang} code reviewed: {Issues} issues, score {Score}/100",
            language, issues.Count, score);

        return result;
    }

    public async Task<CodeReviewResult> ReviewFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var code = await File.ReadAllTextAsync(filePath, ct);
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var lang = ext switch
        {
            "cs" or "csharp" => "csharp",
            "js" or "javascript" => "javascript",
            "ts" or "typescript" => "typescript",
            "py" or "python" => "python",
            "ps1" or "powershell" => "powershell",
            "razor" => "razor",
            "json" => "json",
            "xml" or "csproj" => "xml",
            _ => ext
        };

        return await ReviewCodeAsync(code, lang, ct);
    }

    public IReadOnlyList<CodeReviewRule> GetRules(string? language = null)
    {
        if (language is null) return _rules;
        var lang = language.ToLowerInvariant();
        return _rules.Where(r => r.Languages.Contains("*") || r.Languages.Contains(lang)).ToList();
    }

    private List<CodeIssue> ReviewCSharp(string code, string[] lines)
    {
        var issues = new List<CodeIssue>();

        // Check for async void
        if (Regex.IsMatch(code, @"async\s+void\s+"))
        {
            issues.Add(new CodeIssue
            {
                Severity = IssueSeverity.Error,
                Rule = "CS1001",
                Message = "async void détecté - utiliser async Task à la place",
                Suggestion = "Remplacer 'async void' par 'async Task'"
            });
        }

        // Check for empty catch
        if (Regex.IsMatch(code, @"catch\s*\([^)]*\)\s*\{\s*\}"))
        {
            issues.Add(new CodeIssue
            {
                Severity = IssueSeverity.Warning,
                Rule = "CS1002",
                Message = "Bloc catch vide - les erreurs sont silencieusement ignorées",
                Suggestion = "Ajouter un logging ou rethrow l'exception"
            });
        }

        // Check for magic numbers
        if (Regex.IsMatch(code, @"[^a-zA-Z_]\b\d{4,}\b[^a-zA-Z_""']"))
        {
            issues.Add(new CodeIssue
            {
                Severity = IssueSeverity.Info,
                Rule = "CS1003",
                Message = "Nombre magique détecté - extraire en constante nommée",
                Suggestion = "Définir une constante avec un nom descriptif"
            });
        }

        // Check for nested linq
        if (Regex.IsMatch(code, @"\.Select\(.*\.Select\("))
        {
            issues.Add(new CodeIssue
            {
                Severity = IssueSeverity.Warning,
                Rule = "CS1004",
                Message = "LINQ imbriqué - complexité de lecture élevée",
                Suggestion = "Utiliser des variables intermédiaires ou Refactor"
            });
        }

        return issues;
    }

    private void InitializeRules()
    {
        // Universal rules
        _rules.Add(new CodeReviewRule
        {
            Name = "TODO",
            Pattern = new Regex(@"//\s*TODO", RegexOptions.IgnoreCase),
            Severity = IssueSeverity.Info,
            Message = "Commentaire TODO détecté",
            Suggestion = "Créer un ticket ou implémenter immédiatement",
            Languages = new[] { "*" }
        });

        _rules.Add(new CodeReviewRule
        {
            Name = "HACK",
            Pattern = new Regex(@"//\s*HACK", RegexOptions.IgnoreCase),
            Severity = IssueSeverity.Warning,
            Message = "Solution temporaire (HACK) détectée",
            Suggestion = "Implémenter une solution propre",
            Languages = new[] { "*" }
        });

        _rules.Add(new CodeReviewRule
        {
            Name = "PASSWORD",
            Pattern = new Regex(@"password\s*=\s*"".*""", RegexOptions.IgnoreCase),
            Severity = IssueSeverity.Error,
            Message = "Mot de passe en dur dans le code",
            Suggestion = "Utiliser des variables d'environnement ou Secret Manager",
            Languages = new[] { "*" }
        });

        // C# specific
        _rules.Add(new CodeReviewRule
        {
            Name = "SPAWN",
            Pattern = new Regex(@"new\s+Thread\(|\.Start\(\)"),
            Severity = IssueSeverity.Warning,
            Message = "Création de thread directe détectée",
            Suggestion = "Utiliser Task.Run ou ThreadPool",
            Languages = new[] { "csharp" }
        });
    }

    private string GenerateSummary(List<CodeIssue> issues, int score)
    {
        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        var infos = issues.Count(i => i.Severity == IssueSeverity.Info);

        var grade = score switch
        {
            >= 90 => "A",
            >= 80 => "B",
            >= 70 => "C",
            >= 60 => "D",
            _ => "F"
        };

        return $"Score: {score}/100 ({grade}) | {errors} erreurs, {warnings} avertissements, {infos} infos";
    }
}

public sealed class CodeReviewResult
{
    public string Language { get; set; } = "";
    public int TotalLines { get; set; }
    public List<CodeIssue> Issues { get; set; } = new();
    public int QualityScore { get; set; }
    public string Summary { get; set; } = "";
    public DateTime ReviewedAt { get; set; }
}

public sealed class CodeIssue
{
    public IssueSeverity Severity { get; set; }
    public string Rule { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Suggestion { get; set; }
}

public sealed class CodeReviewRule
{
    public string Name { get; set; } = "";
    public Regex Pattern { get; set; } = null!;
    public IssueSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public string? Suggestion { get; set; }
    public string[] Languages { get; set; } = Array.Empty<string>();
}

public enum IssueSeverity { Info, Warning, Error }
