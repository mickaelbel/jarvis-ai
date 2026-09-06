using JarvisAI.Application.Tools;

namespace JarvisAI.Application.AI;

public static class ToolDefinitionBuilder
{
    private static readonly object _cacheLock = new();
    private static int _cachedVersion = -1;
    private static IReadOnlyList<AIToolDefinition>? _cachedDefinitions;

    public static IReadOnlyList<AIToolDefinition> Build(IToolRegistry registry)
    {
        var version = registry.Version;
        lock (_cacheLock)
        {
            if (_cachedDefinitions is not null && _cachedVersion == version)
                return _cachedDefinitions;
        }
        var defs = Build(registry.GetAll());
        lock (_cacheLock)
        {
            _cachedDefinitions = defs;
            _cachedVersion = version;
        }
        return _cachedDefinitions;
    }

    public static IReadOnlyList<AIToolDefinition> Build(IEnumerable<ITool> tools)
    {
        var definitions = new List<AIToolDefinition>();
        var toolList = tools.Where(t => t.IsAvailable).ToList();

        // When computer_action is available, remove computer_use AND browser
        // to force the model to use computer_action for local app interactions
        var hasComputerAction = toolList.Any(t => t.Name == "computer_action");
        if (hasComputerAction)
        {
            toolList = toolList.Where(t =>
                t.Name != "computer_use" &&
                t.Name != "browser").ToList();
        }

        foreach (var tool in toolList)
        {
            var properties = new Dictionary<string, AIToolProperty>();
            foreach (var param in tool.Parameters)
            {
                properties[param.Name] = new AIToolProperty(
                    type: MapToSchemaType(param.Type),
                    description: param.Description);
            }

            definitions.Add(new AIToolDefinition(
                name: tool.Name,
                description: tool.Description,
                properties: properties,
                required: tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToList()));
        }

        return definitions;
    }

    private static string MapToSchemaType(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.String => "string",
        TypeCode.Boolean => "boolean",
        TypeCode.Int32 or TypeCode.Int64 or TypeCode.Int16 or TypeCode.Byte => "integer",
        TypeCode.Double or TypeCode.Single or TypeCode.Decimal => "number",
        _ => "string"
    };
}
