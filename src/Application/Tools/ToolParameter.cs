namespace JarvisAI.Application.Tools;

public sealed class ToolParameter
{
    public string Name { get; }
    public string Description { get; }
    public Type Type { get; }
    public bool Required { get; }
    public object? DefaultValue { get; }

    public ToolParameter(string name, string description, Type type, bool required = false, object? defaultValue = null)
    {
        Name = name;
        Description = description;
        Type = type;
        Required = required;
        DefaultValue = defaultValue;
    }
}
