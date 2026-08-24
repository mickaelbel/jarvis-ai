using JarvisAI.Application.Tools;

namespace JarvisAI.Application.AI;

public static class ToolDefinitionBuilder
{
    public static IReadOnlyList<AIToolDefinition> Build(IToolRegistry registry)
        => Build(registry.GetAll());

    public static IReadOnlyList<AIToolDefinition> Build(IEnumerable<ITool> tools)
    {
        var definitions = new List<AIToolDefinition>();

        foreach (var tool in tools.Where(t => t.IsAvailable))
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
