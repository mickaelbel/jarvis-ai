namespace JarvisAI.Application.AI;

public sealed class AIToolCall
{
    public string Id { get; }
    public string Name { get; }
    public IReadOnlyDictionary<string, string> Arguments { get; }

    public AIToolCall(string id, string name, IReadOnlyDictionary<string, string> arguments)
    {
        Id = id;
        Name = name;
        Arguments = arguments;
    }
}
