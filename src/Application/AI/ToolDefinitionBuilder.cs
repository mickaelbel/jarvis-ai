using JarvisAI.Application.Tools;

namespace JarvisAI.Application.AI;

public static class ToolDefinitionBuilder
{
    private static int _cachedVersion = -1;
    private static IReadOnlyList<AIToolDefinition>? _cachedDefinitions;

    public static IReadOnlyList<AIToolDefinition> Build(IToolRegistry registry)
    {
        var version = registry.Version;
        if (_cachedDefinitions is not null && _cachedVersion == version)
            return _cachedDefinitions;

        _cachedDefinitions = Build(registry.GetAll());
        _cachedVersion = version;
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
                    type: param.Type.Name.ToLowerInvariant(),
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
}
