using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisAI.Web.Services;

public sealed class ConfigManagerTool : ToolBase
{
    private readonly IEnvironmentManagerService _env;

    public override string Name => "config_manager";
    public override string Description => "Lit et écrit des fichiers de configuration (.env, JSON) et gère les environnements. Actions: list (environnements), read_env, write_env, read_json (navigation par :), write_json, env_export.";
    public override string Category => "system";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "list, read_env, write_env, read_json, write_json, env_export", typeof(string), required: true),
        new ToolParameter("file_path", "Chemin du fichier .env ou JSON", typeof(string)),
        new ToolParameter("json_path", "Chemin JSON séparé par ':' (ex: ConnectionStrings:DefaultConnection)", typeof(string)),
        new ToolParameter("value", "Valeur à écrire (write_json)", typeof(string)),
        new ToolParameter("entries", "Entrées KEY=VALUE séparées par des retours à la ligne (write_env)", typeof(string)),
        new ToolParameter("format", "Format d'export: json ou env (env_export)", typeof(string)),
        new ToolParameter("env_id", "Identifiant d'environnement (optionnel, défaut: actif)", typeof(string)),
    };

    public ConfigManagerTool(IEnvironmentManagerService env, ILogger<ConfigManagerTool> logger) : base(logger)
    {
        _env = env;
    }

    protected override Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        return action switch
        {
            "list" => Task.FromResult(HandleList()),
            "read_env" => Task.FromResult(HandleReadEnv(parameters)),
            "write_env" => Task.FromResult(HandleWriteEnv(parameters)),
            "read_json" => Task.FromResult(HandleReadJson(parameters)),
            "write_json" => Task.FromResult(HandleWriteJson(parameters)),
            "env_export" => Task.FromResult(HandleEnvExport(parameters)),
            _ => Task.FromResult(Fail($"Action inconnue: {action}. Valides: list, read_env, write_env, read_json, write_json, env_export"))
        };
    }

    private ToolResult HandleList()
    {
        var environments = _env.GetEnvironments();
        if (environments.Count == 0)
            return Ok("Aucun environnement configuré.");

        var active = _env.GetActiveEnvironment();
        var sb = new StringBuilder();
        sb.AppendLine($"Environnements ({environments.Count}) :");
        foreach (var e in environments)
        {
            var marker = active is not null && active.Id == e.Id ? " (actif)" : "";
            sb.AppendLine($"- {e.Name} [{e.Id}] : {e.Variables.Count} variable(s){marker}");
        }
        return Ok(sb.ToString());
    }

    private ToolResult HandleReadEnv(IReadOnlyDictionary<string, string> parameters)
    {
        var filePath = RequireParam(parameters, "file_path");
        if (!File.Exists(filePath))
            return Fail($"Fichier introuvable: {filePath}");

        var sb = new StringBuilder();
        sb.AppendLine($"Contenu de {filePath} :");
        foreach (var kv in ParseEnvFile(filePath).OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"- {kv.Key}={kv.Value}");
        return Ok(sb.ToString());
    }

    private ToolResult HandleWriteEnv(IReadOnlyDictionary<string, string> parameters)
    {
        var filePath = RequireParam(parameters, "file_path");
        var entries = RequireParam(parameters, "entries");

        var newValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            newValues[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        if (newValues.Count == 0)
            return Fail("Aucune entrée KEY=VALUE valide dans 'entries'.");

        var comments = new List<string>();
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(filePath))
        {
            foreach (var raw in File.ReadAllLines(filePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    comments.Add(raw);
                    continue;
                }
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                merged[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
        }

        foreach (var kv in newValues)
            merged[kv.Key] = kv.Value;

        var sb = new StringBuilder();
        foreach (var comment in comments)
            sb.AppendLine(comment);
        foreach (var kv in merged.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"{kv.Key}={kv.Value}");

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        return Ok($"Fichier .env écrit: {filePath} ({merged.Count} variable(s)).");
    }

    private ToolResult HandleReadJson(IReadOnlyDictionary<string, string> parameters)
    {
        var filePath = RequireParam(parameters, "file_path");
        if (!File.Exists(filePath))
            return Fail($"Fichier introuvable: {filePath}");

        parameters.TryGetValue("json_path", out var jsonPath);
        var content = File.ReadAllText(filePath);

        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            using var fullDoc = JsonDocument.Parse(content);
            return Ok($"Arborescence de {filePath} :\n{fullDoc.RootElement.GetRawText()}");
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            var element = Navigate(doc.RootElement, jsonPath);
            if (element is null)
                return Fail($"Chemin JSON introuvable: {jsonPath}");
            var raw = element.Value.ValueKind == JsonValueKind.String ? element.Value.GetString() : element.Value.GetRawText();
            return Ok($"{jsonPath} = {raw}");
        }
        catch (JsonException ex)
        {
            return Fail($"JSON invalide: {ex.Message}");
        }
    }

    private ToolResult HandleWriteJson(IReadOnlyDictionary<string, string> parameters)
    {
        var filePath = RequireParam(parameters, "file_path");
        if (!File.Exists(filePath))
            return Fail($"Fichier introuvable: {filePath}");

        var jsonPath = RequireParam(parameters, "json_path");
        var value = RequireParam(parameters, "value");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(filePath));
        }
        catch (JsonException ex)
        {
            return Fail($"JSON invalide: {ex.Message}");
        }
        if (root is null)
            return Fail("Fichier JSON vide ou invalide.");

        var target = NavigateNode(root, jsonPath, out var lastSegment);
        if (target is not JsonObject obj || string.IsNullOrWhiteSpace(lastSegment))
            return Fail($"Impossible d'écrire au chemin: {jsonPath}");

        var parsed = JsonNode.Parse(value) ?? JsonValue.Create(value);
        obj[lastSegment] = parsed;

        var output = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, output);
        return Ok($"Valeur écrite dans {filePath} : {jsonPath} = {value}");
    }

    private ToolResult HandleEnvExport(IReadOnlyDictionary<string, string> parameters)
    {
        var format = parameters.TryGetValue("format", out var f) && !string.IsNullOrWhiteSpace(f) ? f : "json";
        if (!format.Equals("json", StringComparison.OrdinalIgnoreCase) && !format.Equals("env", StringComparison.OrdinalIgnoreCase))
            return Fail($"Format inconnu: {format}. Valides: json, env");

        var envId = parameters.TryGetValue("env_id", out var eid) && !string.IsNullOrWhiteSpace(eid)
            ? eid
            : _env.GetActiveEnvironment()?.Id;
        if (string.IsNullOrWhiteSpace(envId))
            return Fail("Aucun environnement actif (précisez env_id).");
        if (_env.GetEnvironment(envId) is null)
            return Fail($"Environnement introuvable: {envId}");

        var exported = _env.ExportEnvironment(envId, format);
        return Ok($"Export de l'environnement '{envId}' au format {format} :\n{exported}");
    }

    private static JsonElement? Navigate(JsonElement root, string jsonPath)
    {
        var element = root;
        foreach (var segment in jsonPath.Split(':'))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!element.TryGetProperty(segment, out element))
                        return null;
                    break;
                case JsonValueKind.Array:
                    if (!int.TryParse(segment, out var index) || index < 0 || index >= element.GetArrayLength())
                        return null;
                    element = element[index];
                    break;
                default:
                    return null;
            }
        }
        return element;
    }

    private static JsonNode? NavigateNode(JsonNode root, string jsonPath, out string lastSegment)
    {
        var segments = jsonPath.Split(':');
        lastSegment = segments.Length > 0 ? segments[^1] : "";
        if (segments.Length <= 1)
            return root;

        var current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current is not JsonObject obj)
                return null;
            if (obj[segments[i]] is null)
                obj[segments[i]] = new JsonObject();
            current = obj[segments[i]];
        }
        return current;
    }

    private static Dictionary<string, string> ParseEnvFile(string filePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(filePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            result[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return result;
    }
}