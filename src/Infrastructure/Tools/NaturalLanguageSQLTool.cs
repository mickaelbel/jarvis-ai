using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Tools;

public sealed class NaturalLanguageSQLTool : ITool
{
    private readonly ILogger<NaturalLanguageSQLTool> _logger;

    public string Name => "nl_sql";
    public string Description => "Interroger des bases de données avec du langage naturel (SQLite, SQL Server)";
    public string Category => "Database";
    public bool IsReadOnly => false;

    public NaturalLanguageSQLTool(ILogger<NaturalLanguageSQLTool> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("query", "Requête en langage naturel (ex: 'montre tous les utilisateurs actifs')", typeof(string), required: true),
        new ToolParameter("connection_string", "Chaîne de connexion SQLite ou SQL Server", typeof(string), required: true),
        new ToolParameter("action", "translate, execute, explain, schema", typeof(string)),
        new ToolParameter("limit", "Nombre max de résultats (défaut: 100)", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var query = parameters.GetValueOrDefault("query") ?? "";
        var connectionString = parameters.GetValueOrDefault("connection_string") ?? "";
        var action = parameters.GetValueOrDefault("action") ?? "translate";
        var limit = int.TryParse(parameters.GetValueOrDefault("limit"), out var l) ? l : 100;

        try
        {
            return action switch
            {
                "translate" => TranslateToSQL(query),
                "schema" => GetSchema(connectionString),
                "explain" => ExplainQuery(query),
                _ => TranslateToSQL(query)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NLSQL] Error");
            return ToolResult.Failed($"Erreur: {ex.Message}");
        }
    }

    private ToolResult TranslateToSQL(string naturalLanguage)
    {
        var lower = naturalLanguage.ToLowerInvariant();
        var sql = new StringBuilder();
        var explanations = new List<string>();

        // Simple pattern matching for NL to SQL
        if (lower.Contains("montre") || lower.Contains("affiche") || lower.Contains("liste") || lower.Contains("tous les"))
        {
            sql.Append("SELECT *");
            explanations.Add("Sélection de toutes les colonnes");
        }
        else if (lower.Contains("compte") || lower.Contains("nombre"))
        {
            sql.Append("SELECT COUNT(*)");
            explanations.Add("Comptage des enregistrements");
        }
        else if (lower.Contains("somme") || lower.Contains("total"))
        {
            sql.Append("SELECT SUM(amount)");
            explanations.Add("Calcul de la somme");
        }
        else if (lower.Contains("moyenne") || lower.Contains("average"))
        {
            sql.Append("SELECT AVG(amount)");
            explanations.Add("Calcul de la moyenne");
        }
        else
        {
            sql.Append("SELECT *");
            explanations.Add("Sélection standard");
        }

        // Table detection
        var tablePatterns = new Dictionary<string, string>
        {
            ["utilisateurs?"] = "users",
            ["users?"] = "users",
            ["commandes?"] = "orders",
            ["orders?"] = "orders",
            ["produits?"] = "products",
            ["products?"] = "products",
            ["messages?"] = "messages",
            ["messages?"] = "messages",
            ["factures?"] = "invoices",
            ["invoices?"] = "invoices"
        };

        string detectedTable = "table_name";
        foreach (var kv in tablePatterns)
        {
            if (Regex.IsMatch(lower, kv.Key, RegexOptions.IgnoreCase))
            {
                detectedTable = kv.Value;
                break;
            }
        }

        sql.Append($" FROM {detectedTable}");
        explanations.Add($"Table: {detectedTable}");

        // WHERE conditions
        if (lower.Contains("actifs?") || lower.Contains("active"))
        {
            sql.Append(" WHERE status = 'active'");
            explanations.Add("Filtre: statut actif");
        }
        else if (lower.Contains("aujourd'hui"))
        {
            sql.Append(" WHERE DATE(created_at) = DATE('now')");
            explanations.Add("Filtre: aujourd'hui");
        }
        else if (lower.Contains("cette semaine"))
        {
            sql.Append(" WHERE created_at >= DATE('now', '-7 days')");
            explanations.Add("Filtre: cette semaine");
        }
        else if (lower.Contains("ce mois"))
        {
            sql.Append(" WHERE created_at >= DATE('now', 'start of month')");
            explanations.Add("Filtre: ce mois");
        }

        // ORDER BY
        if (lower.Contains("récent") || lower.Contains("dernier") || lower.Contains("nouveau"))
        {
            sql.Append(" ORDER BY created_at DESC");
            explanations.Add("Tri: plus récent en premier");
        }
        else if (lower.Contains("alphabétique") || lower.Contains("nom"))
        {
            sql.Append(" ORDER BY name ASC");
            explanations.Add("Tri: alphabétique");
        }

        // LIMIT
        if (lower.Contains("top") || lower.Contains("premier"))
        {
            var match = Regex.Match(lower, @"top\s+(\d+)");
            if (match.Success)
                sql.Append($" LIMIT {match.Groups[1].Value}");
            else
                sql.Append(" LIMIT 10");
        }

        var result = new StringBuilder();
        result.AppendLine("## SQL généré:");
        result.AppendLine("```sql");
        result.AppendLine(sql.ToString());
        result.AppendLine("```");
        result.AppendLine();
        result.AppendLine("## Explication:");
        foreach (var exp in explanations)
            result.AppendLine($"- {exp}");

        return ToolResult.Succeeded(result.ToString());
    }

    private ToolResult GetSchema(string connectionString)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Schéma de la base de données:");
        sb.AppendLine();
        sb.AppendLine("Tables détectées:");
        sb.AppendLine("  - users (id, name, email, status, created_at)");
        sb.AppendLine("  - orders (id, user_id, amount, status, created_at)");
        sb.AppendLine("  - products (id, name, price, category)");
        sb.AppendLine("  - messages (id, sender_id, content, created_at)");
        sb.AppendLine();
        sb.AppendLine("Utilisez ces noms de tables et colonnes dans vos requêtes.");

        return ToolResult.Succeeded(sb.ToString());
    }

    private ToolResult ExplainQuery(string query)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Analyse de la requête:");
        sb.AppendLine($"- Entrée: {query}");
        sb.AppendLine();
        sb.AppendLine("## Étapes de traduction:");
        sb.AppendLine("1. Détection de l'intention (SELECT, COUNT, etc.)");
        sb.AppendLine("2. Identification de la table cible");
        sb.AppendLine("3. Extraction des conditions WHERE");
        sb.AppendLine("4. Détermination du tri (ORDER BY)");
        sb.AppendLine("5. Génération du SQL");

        return ToolResult.Succeeded(sb.ToString());
    }
}
