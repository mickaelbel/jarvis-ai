using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using System.Text.Json;

namespace JarvisAI.Application.AutoImprovement;

/// <summary>
/// Méta-outil : permet à l'IA de se créer un nouvel outil (ou d'en améliorer un
/// existant) quand elle constate qu'une capacité lui manque. Risque ÉLEVÉ →
/// exige une confirmation utilisateur à chaque création.
/// </summary>
public sealed class CreateToolTool : ITool
{
    private readonly ISelfImprovementManager _manager;

    public string Name => "create_tool";
    public string Description =>
        "Create or upgrade your own tool when you notice you are missing a capability. " +
        "Supply a name, description, parameters and the implementation. " +
        "Two runtimes: 'recipe' (an ordered list of steps calling existing tools OR other skills you already created; " +
        "steps = JSON array of {\"toolName\": string, \"arguments\": {param: value}, each value may " +
        "reference tool input params as {name}}) or 'csharp' (plain C# body run inside a sandbox; " +
        "use the 'host' IToolHost object: host.ReadTextFile(path), host.WriteTextFile(path, content), " +
        "host.HttpGet(url), host.NowUtc(), host.Log(msg); file access is restricted to the workspace). " +
        "The code must not touch processes, reflection, the file system directly, or the network " +
        "except via host.* . Always use this when you lack a tool for the user's request.";
    public string Category => "self-improvement";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("name", "Unique lowercase tool name (letters/digits/underscore).", typeof(string), true),
        new ToolParameter("description", "What the tool does, when to use it.", typeof(string), true),
        new ToolParameter("category", "Optional category label.", typeof(string)),
        new ToolParameter("riskLevel", "low | medium | high. Declared risk of the new tool.", typeof(string)),
        new ToolParameter("runtime", "recipe | csharp", typeof(string), true),
        new ToolParameter("parametersJson", "JSON array of {name, description, type: string|number|boolean, required}.", typeof(string)),
        new ToolParameter("stepsJson", "For 'recipe': JSON array of {toolName, arguments:{key:value}}.", typeof(string)),
        new ToolParameter("code", "For 'csharp': the C# implementation body (return a string).", typeof(string)),
        new ToolParameter("reason", "Why this tool is needed.", typeof(string))
    };

    public CreateToolTool(ISelfImprovementManager manager) => _manager = manager;

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        string Get(string key) => parameters.TryGetValue(key, out var v) ? v.Trim() : "";

        var spec = new AutoToolSpec
        {
            Name = Get("name"),
            Description = Get("description"),
            Category = Get("category"),
            RiskLevel = Get("riskLevel"),
            Runtime = Get("runtime"),
            Code = Get("code"),
            Version = "1"
        };

        var parsedParams = ParseParams(Get("parametersJson"));
        if (parsedParams is null)
            return Task.FromResult(ToolResult.Failed("parametersJson is not valid JSON."));
        spec.Parameters = parsedParams;

        if (spec.Runtime == AutoToolRuntimes.Recipe)
        {
            var steps = ParseSteps(Get("stepsJson"));
            if (steps is null)
                return Task.FromResult(ToolResult.Failed("stepsJson is not valid JSON."));
            spec.Steps = steps;
        }

        var result = _manager.CreateTool(spec, "ai");
        return Task.FromResult(
            result.Success
                ? ToolResult.Succeeded($"Tool '{result.Name}' created and now available. Version: {spec.Version}.")
                : ToolResult.Failed($"Tool not created: {result.Error}"));
    }

    private static List<AutoToolParameterDef>? ParseParams(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<AutoToolParameterDef>>(json, JsonOptions)
                ?? new List<AutoToolParameterDef>();
        }
        catch { return null; }
    }

    private static List<AutoToolRecipeStep>? ParseSteps(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<AutoToolRecipeStep>>(json, JsonOptions)
                ?? new List<AutoToolRecipeStep>();
        }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
