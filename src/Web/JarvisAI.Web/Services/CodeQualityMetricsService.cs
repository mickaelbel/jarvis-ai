using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ICodeQualityMetricsService
{
    CodeQualityReport AnalyzeCode(string code, string language);
    IReadOnlyList<CodeQualityReport> GetHistory(string? filePath = null);
    QualityTrend GetTrend(int days = 30);
}

public sealed class CodeQualityMetricsService : ICodeQualityMetricsService
{
    private readonly ILogger<CodeQualityMetricsService> _logger;
    private readonly string _storagePath;
    private readonly List<CodeQualityReport> _reports = new();

    public CodeQualityMetricsService(ILogger<CodeQualityMetricsService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "code_quality.json");
        Load();
    }

    public CodeQualityReport AnalyzeCode(string code, string language)
    {
        var lines = code.Split('\n');
        var lang = language.ToLowerInvariant();

        var report = new CodeQualityReport
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Language = language,
            TotalLines = lines.Length,
            CodeLines = lines.Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("#")),
            CommentLines = lines.Count(l => l.TrimStart().StartsWith("//") || l.TrimStart().StartsWith("#")),
            EmptyLines = lines.Count(l => string.IsNullOrWhiteSpace(l)),
            MaxLineLength = lines.Max(l => l.Length),
            AverageLineLength = lines.Average(l => l.Length),
            ComplexityScore = CalculateComplexity(code, lang),
            MaintainabilityIndex = CalculateMaintainability(lines.Length, lang),
            Issues = DetectIssues(code, lang),
            AnalyzedAt = DateTime.UtcNow
        };

        _reports.Add(report);

        // Keep only last 100 reports
        if (_reports.Count > 100)
            _reports.RemoveRange(0, _reports.Count - 100);

        Save();
        return report;
    }

    public IReadOnlyList<CodeQualityReport> GetHistory(string? filePath = null)
    {
        if (filePath is null) return _reports.OrderByDescending(r => r.AnalyzedAt).ToList();
        return _reports.Where(r => r.FilePath == filePath).OrderByDescending(r => r.AnalyzedAt).ToList();
    }

    public QualityTrend GetTrend(int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var recent = _reports.Where(r => r.AnalyzedAt >= cutoff).ToList();

        if (recent.Count < 2)
            return new QualityTrend { Direction = TrendDirection.Stable, ChangePercentage = 0 };

        var firstHalf = recent.Take(recent.Count / 2).Average(r => r.MaintainabilityIndex);
        var secondHalf = recent.Skip(recent.Count / 2).Average(r => r.MaintainabilityIndex);
        var change = ((secondHalf - firstHalf) / firstHalf) * 100;

        return new QualityTrend
        {
            Direction = change > 5 ? TrendDirection.Improving :
                       change < -5 ? TrendDirection.Degrading :
                       TrendDirection.Stable,
            ChangePercentage = change,
            AverageScore = recent.Average(r => r.MaintainabilityIndex),
            TotalAnalyses = recent.Count
        };
    }

    private int CalculateComplexity(string code, string language)
    {
        var complexity = 0;
        var lower = code.ToLowerInvariant();

        // Count control flow structures
        complexity += CountOccurrences(lower, "if ") * 2;
        complexity += CountOccurrences(lower, "else if ") * 3;
        complexity += CountOccurrences(lower, "for ") * 2;
        complexity += CountOccurrences(lower, "foreach ") * 2;
        complexity += CountOccurrences(lower, "while ") * 2;
        complexity += CountOccurrences(lower, "switch ") * 3;
        complexity += CountOccurrences(lower, "case ") * 1;
        complexity += CountOccurrences(lower, "catch ") * 2;
        complexity += CountOccurrences(lower, "&& ") * 1;
        complexity += CountOccurrences(lower, "|| ") * 1;
        complexity += CountOccurrences(lower, "?? ") * 1;
        complexity += CountOccurrences(lower, "? ") * 1;

        return Math.Min(100, complexity);
    }

    private double CalculateMaintainability(int lineCount, string language)
    {
        // Simple maintainability index (0-100, higher is better)
        var baseScore = 100.0;
        var linePenalty = Math.Max(0, lineCount - 100) * 0.1;
        return Math.Max(0, baseScore - linePenalty);
    }

    private List<QualityIssue> DetectIssues(string code, string language)
    {
        var issues = new List<QualityIssue>();

        // Long methods
        var methods = code.Split(new[] { "public ", "private ", "protected ", "internal " }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var method in methods)
        {
            var methodLines = method.Split('\n').Length;
            if (methodLines > 50)
            {
                issues.Add(new QualityIssue
                {
                    Severity = IssueSeverity.Warning,
                    Type = "LongMethod",
                    Message = $"Méthode trop longue ({methodLines} lignes)",
                    Recommendation = "Diviser en sous-méthodes plus petites"
                });
            }
        }

        // Too many parameters
        var paramMatches = System.Text.RegularExpressions.Regex.Matches(code, @"(?:public|private|protected)\s+\w+\s+\w+\s*\(([^)]+)\)");
        foreach (System.Text.RegularExpressions.Match match in paramMatches)
        {
            var paramCount = match.Groups[1].Value.Split(',').Length;
            if (paramCount > 5)
            {
                issues.Add(new QualityIssue
                {
                    Severity = IssueSeverity.Warning,
                    Type = "TooManyParameters",
                    Message = $"Trop de paramètres ({paramCount})",
                    Recommendation = "Utiliser un objet de configuration ou un builder"
                });
            }
        }

        // Nested code
        var maxNesting = CalculateMaxNesting(code);
        if (maxNesting > 4)
        {
            issues.Add(new QualityIssue
            {
                Severity = IssueSeverity.Warning,
                Type = "DeepNesting",
                Message = $"Imbrication profonde ({maxNesting} niveaux)",
                Recommendation = "Extraire en méthodes ou inverser les conditions"
            });
        }

        return issues;
    }

    private int CalculateMaxNesting(string code)
    {
        var maxNesting = 0;
        var currentNesting = 0;

        foreach (var c in code)
        {
            if (c == '{')
            {
                currentNesting++;
                maxNesting = Math.Max(maxNesting, currentNesting);
            }
            else if (c == '}')
            {
                currentNesting--;
            }
        }

        return maxNesting;
    }

    private int CountOccurrences(string source, string substring)
    {
        int count = 0, index = 0;
        while ((index = source.IndexOf(substring, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<CodeQualityReport>>(json);
                if (loaded is not null) _reports.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(_reports, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class CodeQualityReport
{
    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Language { get; set; } = "";
    public int TotalLines { get; set; }
    public int CodeLines { get; set; }
    public int CommentLines { get; set; }
    public int EmptyLines { get; set; }
    public int MaxLineLength { get; set; }
    public double AverageLineLength { get; set; }
    public int ComplexityScore { get; set; }
    public double MaintainabilityIndex { get; set; }
    public List<QualityIssue> Issues { get; set; } = new();
    public DateTime AnalyzedAt { get; set; }
}

public sealed class QualityIssue
{
    public IssueSeverity Severity { get; set; }
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public string Recommendation { get; set; } = "";
}

public sealed class QualityTrend
{
    public TrendDirection Direction { get; set; }
    public double ChangePercentage { get; set; }
    public double AverageScore { get; set; }
    public int TotalAnalyses { get; set; }
}

public enum TrendDirection { Improving, Stable, Degrading }
public enum IssueSeverity { Info, Warning, Error }
