namespace JarvisAI.Application.Agents;

public sealed class AgentContext
{
    public Guid CorrelationId { get; } = Guid.NewGuid();
    public string CommandText { get; }
    public string? Source { get; }
    public Dictionary<string, object> Metadata { get; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public AgentContext(string commandText, string? source = null, Dictionary<string, object>? metadata = null)
    {
        CommandText = commandText;
        Source = source;
        Metadata = metadata ?? new();
    }
}
