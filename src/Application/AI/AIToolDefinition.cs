namespace JarvisAI.Application.AI;

public sealed class AIToolDefinition
{
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyDictionary<string, AIToolProperty> Properties { get; }
    public IReadOnlyList<string> Required { get; }

    public AIToolDefinition(string name, string description, IReadOnlyDictionary<string, AIToolProperty> properties, IReadOnlyList<string>? required = null)
    {
        Name = name;
        Description = description;
        Properties = properties;
        Required = required ?? Array.Empty<string>();
    }
}

public sealed class AIToolProperty
{
    public string Type { get; }
    public string Description { get; }

    public AIToolProperty(string type, string description)
    {
        Type = type;
        Description = description;
    }
}
