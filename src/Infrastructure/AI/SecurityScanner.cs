using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.AI;

public interface ISecurityScanner
{
    SecurityScanResult ScanCode(string code, string language);
    SecurityScanResult ScanFile(string filePath);
    IReadOnlyList<SecurityRule> GetRules(string? language = null);
}

public sealed class SecurityScanner : ISecurityScanner
{
    private readonly ILogger<SecurityScanner> _logger;
    private readonly List<SecurityRule> _rules = new();

    public SecurityScanner(ILogger<SecurityScanner> logger)
    {
        _logger = logger;
        InitializeRules();
    }

    public SecurityScanResult ScanCode(string code, string language)
    {
        var findings = new List<SecurityFinding>();
        var lang = language.ToLowerInvariant();

        foreach (var rule in _rules.Where(r => r.Languages.Contains("*") || r.Languages.Contains(lang)))
        {
            var matches = rule.Pattern.Matches(code);
            foreach (Match match in matches)
            {
                findings.Add(new SecurityFinding
                {
                    RuleId = rule.Id,
                    Severity = rule.Severity,
                    Title = rule.Title,
                    Description = rule.Description,
                    Recommendation = rule.Recommendation,
                    LineNumber = code.Substring(0, match.Index).Count(c => c == '\n') + 1,
                    CodeSnippet = match.Value
                });
            }
        }

        var result = new SecurityScanResult
        {
            Language = language,
            Findings = findings,
            Score = CalculateScore(findings),
            RiskLevel = CalculateRiskLevel(findings),
            ScannedAt = DateTime.UtcNow
        };

        _logger.LogInformation("[Security] {Lang} scan: {Findings} findings, score {Score}",
            language, findings.Count, result.Score);

        return result;
    }

    public SecurityScanResult ScanFile(string filePath)
    {
        if (!File.Exists(filePath))
            return new SecurityScanResult { Findings = new(), Score = 100, RiskLevel = SecurityRisk.Low };

        var code = File.ReadAllText(filePath);
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var lang = ext switch
        {
            "cs" => "csharp",
            "js" => "javascript",
            "ts" => "typescript",
            "py" => "python",
            _ => ext
        };

        var result = ScanCode(code, lang);
        result.FilePath = filePath;
        return result;
    }

    public IReadOnlyList<SecurityRule> GetRules(string? language = null)
    {
        if (language is null) return _rules;
        return _rules.Where(r => r.Languages.Contains("*") || r.Languages.Contains(language)).ToList();
    }

    private int CalculateScore(List<SecurityFinding> findings)
    {
        var score = 100;
        foreach (var finding in findings)
        {
            score -= finding.Severity switch
            {
                SecuritySeverity.Critical => 25,
                SecuritySeverity.High => 15,
                SecuritySeverity.Medium => 8,
                SecuritySeverity.Low => 3,
                _ => 1
            };
        }
        return Math.Max(0, score);
    }

    private SecurityRisk CalculateRiskLevel(List<SecurityFinding> findings)
    {
        if (findings.Any(f => f.Severity == SecuritySeverity.Critical))
            return SecurityRisk.Critical;
        if (findings.Any(f => f.Severity == SecuritySeverity.High))
            return SecurityRisk.High;
        if (findings.Any(f => f.Severity == SecuritySeverity.Medium))
            return SecurityRisk.Medium;
        return SecurityRisk.Low;
    }

    private void InitializeRules()
    {
        _rules.AddRange(new[]
        {
            new SecurityRule
            {
                Id = "SQL001",
                Title = "SQL Injection potentielle",
                Description = "Utilisation de string.Format ou concaténation pour construire des requêtes SQL",
                Pattern = new Regex(@"(?:string\.Format|""\s*\+\s*|SELECT\s+.*\s+FROM\s+.*\s+WHERE\s+.*\s*=.*\+)", RegexOptions.IgnoreCase),
                Severity = SecuritySeverity.Critical,
                Recommendation = "Utiliser des requêtes paramétrées ou Entity Framework",
                Languages = new[] { "csharp", "javascript", "python" }
            },
            new SecurityRule
            {
                Id = "XSS001",
                Title = "Cross-Site Scripting potentiel",
                Description = "Rendu de contenu utilisateur non échappé",
                Pattern = new Regex(@"(?:innerHTML|dangerouslySetInnerHTML|\.Html\(|@\()"),
                Severity = SecuritySeverity.High,
                Recommendation = "Échapper le contenu utilisateur avant rendu",
                Languages = new[] { "csharp", "javascript" }
            },
            new SecurityRule
            {
                Id = "CRYPTO001",
                Title = "Algorithme de hash faible",
                Description = "Utilisation de MD5 ou SHA1 pour le hachage",
                Pattern = new Regex(@"(?:MD5\.Create|SHA1\.Create| hashlib\.md5| hashlib\.sha1)", RegexOptions.IgnoreCase),
                Severity = SecuritySeverity.High,
                Recommendation = "Utiliser SHA256 ou bcrypt pour les mots de passe",
                Languages = new[] { "csharp", "python" }
            },
            new SecurityRule
            {
                Id = "SECRET001",
                Title = "Secret en dur dans le code",
                Description = "Mot de passe ou clé API trouvé en dur",
                Pattern = new Regex(@"(?:password|api_key|secret|token)\s*=\s*"".+""", RegexOptions.IgnoreCase),
                Severity = SecuritySeverity.Critical,
                Recommendation = "Utiliser des variables d'environnement ou Secret Manager",
                Languages = new[] { "csharp", "javascript", "python" }
            },
            new SecurityRule
            {
                Id = "PATH001",
                Title = "Path traversal potentiel",
                Description = "Chemin de fichier construit à partir d'une entrée utilisateur",
                Pattern = new Regex(@"(?:File\.Read|File\.Write|File\.Open|StreamReader|StreamWriter).*\+"),
                Severity = SecuritySeverity.Medium,
                Recommendation = "Valider et assainir les chemins de fichier",
                Languages = new[] { "csharp" }
            },
            new SecurityRule
            {
                Id = "XXE001",
                Title = "XML External Entity potentiel",
                Description = "Parsing XML sans désactivation des entités externes",
                Pattern = new Regex(@"(?:XmlDocument|XmlReader|XDocument).*(?:DtdProcessing|ProhibitDtd\s*=\s*false)", RegexOptions.IgnoreCase),
                Severity = SecuritySeverity.High,
                Recommendation = "Désactiver le traitement DTD",
                Languages = new[] { "csharp" }
            },
            new SecurityRule
            {
                Id = "DEBUG001",
                Title = "Information de debug exposée",
                Description = "Exception stack trace exposée en production",
                Pattern = new Regex(@"(?:StackTrace| stack_trace|\.Exception\.Message)", RegexOptions.IgnoreCase),
                Severity = SecuritySeverity.Medium,
                Recommendation = "Ne pas exposer les détails d'erreur en production",
                Languages = new[] { "csharp", "javascript" }
            },
            new SecurityRule
            {
                Id = "RAND001",
                Title = "Génération aléatoire non sécurisée",
                Description = "Utilisation de Random pour des besoins de sécurité",
                Pattern = new Regex(@"new\s+Random\(\)|random\.random\(\)|Math\.random\(\)"),
                Severity = SecuritySeverity.Medium,
                Recommendation = "Utiliser RandomNumberGenerator pour des besoins cryptographiques",
                Languages = new[] { "csharp", "javascript", "python" }
            }
        });
    }
}

public sealed class SecurityScanResult
{
    public string? FilePath { get; set; }
    public string Language { get; set; } = "";
    public List<SecurityFinding> Findings { get; set; } = new();
    public int Score { get; set; }
    public SecurityRisk RiskLevel { get; set; }
    public DateTime ScannedAt { get; set; }
}

public sealed class SecurityFinding
{
    public string RuleId { get; set; } = "";
    public SecuritySeverity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public int LineNumber { get; set; }
    public string CodeSnippet { get; set; } = "";
}

public sealed class SecurityRule
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public Regex Pattern { get; set; } = null!;
    public SecuritySeverity Severity { get; set; }
    public string Recommendation { get; set; } = "";
    public string[] Languages { get; set; } = Array.Empty<string>();
}

public enum SecuritySeverity { Info, Low, Medium, High, Critical }
public enum SecurityRisk { Low, Medium, High, Critical }
