using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.AI;

public interface IQueryOptimizer
{
    OptimizationResult AnalyzeQuery(string query);
    string OptimizeQuery(string query);
    IReadOnlyList<IndexSuggestion> SuggestIndexes(string query);
    string ExplainPlan(string query);
}

public sealed class QueryOptimizer : IQueryOptimizer
{
    private readonly ILogger<QueryOptimizer> _logger;

    public QueryOptimizer(ILogger<QueryOptimizer> logger)
    {
        _logger = logger;
    }

    public OptimizationResult AnalyzeQuery(string query)
    {
        var issues = new List<QueryIssue>();
        var lower = query.ToLowerInvariant();

        // Detect SELECT *
        if (Regex.IsMatch(lower, @"select\s+\*\s+from"))
        {
            issues.Add(new QueryIssue
            {
                Type = IssueType.FullTableScan,
                Severity = IssueSeverity.Warning,
                Description = "SELECT * détecté - récupère toutes les colonnes",
                Recommendation = "Sélectionner uniquement les colonnes nécessaires"
            });
        }

        // Detect missing WHERE
        if (lower.Contains("select") && !lower.Contains("where") && !lower.Contains("group by"))
        {
            issues.Add(new QueryIssue
            {
                Type = IssueType.MissingWhere,
                Severity = IssueSeverity.Warning,
                Description = "Pas de clause WHERE - retourne toutes les lignes",
                Recommendation = "Ajouter un filtre WHERE pour réduire les résultats"
            });
        }

        // Detect LIKE with leading wildcard
        if (Regex.IsMatch(lower, @"like\s+'%"))
        {
            issues.Add(new QueryIssue
            {
                Type = IssueType.LeadingWildcard,
                Severity = IssueSeverity.Warning,
                Description = "LIKE avec wildcard au début - empêche l'utilisation des index",
                Recommendation = "Éviter les wildcard au début ou utiliser FULLTEXT"
            });
        }

        // Detect OR in WHERE (might be better as UNION)
        if (Regex.IsMatch(lower, @"where.*\bor\b"))
        {
            issues.Add(new QueryIssue
            {
                Type = IssueType.SuboptimalOr,
                Severity = IssueSeverity.Info,
                Description = "OR dans WHERE peut être moins performant qu'UNION",
                Recommendation = "Considérer UNION au lieu de OR pour de meilleures performances"
            });
        }

        // Detect N+1 pattern (subquery in SELECT)
        if (Regex.IsMatch(lower, @"select.*\(select"))
        {
            issues.Add(new QueryIssue
            {
                Type = IssueType.NPlusOne,
                Severity = IssueSeverity.Warning,
                Description = "Sous-requête dans SELECT - pattern N+1 potentiel",
                Recommendation = "Utiliser JOIN au lieu de sous-requêtes"
            });
        }

        // Detect ORDER BY without LIMIT
        if (lower.Contains("order by") && !lower.Contains("limit") && !lower.Contains("top"))
        {
            issues.Add(new QueryIssue
            {
                Type = IssueType.UnboundedSort,
                Severity = IssueSeverity.Info,
                Description = "ORDER BY sans LIMIT - trie toutes les résultats",
                Recommendation = "Ajouter LIMIT/TOP pour réduire le tri"
            });
        }

        var score = Math.Max(0, 100 - (issues.Count * 15));

        return new OptimizationResult
        {
            OriginalQuery = query,
            Issues = issues,
            Score = score,
            AnalyzedAt = DateTime.UtcNow
        };
    }

    public string OptimizeQuery(string query)
    {
        var optimized = query;

        // Replace SELECT * with specific columns (placeholder)
        optimized = Regex.Replace(optimized, @"SELECT\s+\*\s+FROM", "SELECT [columns] FROM", RegexOptions.IgnoreCase);

        // Add LIMIT if missing
        if (!optimized.ToLowerInvariant().Contains("limit") && !optimized.ToLowerInvariant().Contains("top"))
        {
            optimized = optimized.TrimEnd(';') + "\nLIMIT 1000;";
        }

        return optimized;
    }

    public IReadOnlyList<IndexSuggestion> SuggestIndexes(string query)
    {
        var suggestions = new List<IndexSuggestion>();

        // Extract WHERE columns
        var whereMatch = Regex.Match(query, @"WHERE\s+(.+?)(?:ORDER|GROUP|LIMIT|$)", RegexOptions.IgnoreCase);
        if (whereMatch.Success)
        {
            var whereClause = whereMatch.Groups[1].Value;
            var columns = Regex.Matches(whereClause, @"(\w+)\s*(?:=|IN|LIKE|>)");

            foreach (Match col in columns)
            {
                suggestions.Add(new IndexSuggestion
                {
                    Table = "table_name",
                    Column = col.Groups[1].Value,
                    IndexType = IndexType.BTree,
                    Reason = "Colonne utilisée dans WHERE"
                });
            }
        }

        // Extract ORDER BY columns
        var orderMatch = Regex.Match(query, @"ORDER\s+BY\s+(\w+)", RegexOptions.IgnoreCase);
        if (orderMatch.Success)
        {
            suggestions.Add(new IndexSuggestion
            {
                Table = "table_name",
                Column = orderMatch.Groups[1].Value,
                IndexType = IndexType.BTree,
                Reason = "Colonne utilisée dans ORDER BY"
            });
        }

        return suggestions;
    }

    public string ExplainPlan(string query)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Analyse du plan d'exécution:");
        sb.AppendLine();
        sb.AppendLine("Requête analysée:");
        sb.AppendLine("```sql");
        sb.AppendLine(query);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Étapes estimées:");
        sb.AppendLine("1. Scan de la table");
        sb.AppendLine("2. Application des filtres WHERE");
        sb.AppendLine("3. Tri des résultats");
        sb.AppendLine("4. Retour des résultats");

        return sb.ToString();
    }
}

public sealed class OptimizationResult
{
    public string OriginalQuery { get; set; } = "";
    public List<QueryIssue> Issues { get; set; } = new();
    public int Score { get; set; }
    public DateTime AnalyzedAt { get; set; }
}

public sealed class QueryIssue
{
    public IssueType Type { get; set; }
    public IssueSeverity Severity { get; set; }
    public string Description { get; set; } = "";
    public string Recommendation { get; set; } = "";
}

public sealed class IndexSuggestion
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public IndexType IndexType { get; set; }
    public string Reason { get; set; } = "";
}

public enum IssueType { FullTableScan, MissingWhere, LeadingWildcard, SuboptimalOr, NPlusOne, UnboundedSort }
public enum IndexType { BTree, Hash, FullText }
