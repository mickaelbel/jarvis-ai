namespace JarvisAI.Application.AI;

public sealed class AIRequest
{
    public string SystemPrompt { get; }
    public IReadOnlyList<AIMessage> Messages { get; }
    public IReadOnlyList<AIToolDefinition> Tools { get; }
    public string Model { get; }
    public float Temperature { get; }
    public int MaxTokens { get; }

    public AIRequest(
        string systemPrompt,
        IReadOnlyList<AIMessage> messages,
        IReadOnlyList<AIToolDefinition>? tools = null,
        string? model = null,
        float temperature = 0.7f,
        int maxTokens = 2048)
    {
        SystemPrompt = systemPrompt;
        Messages = messages;
        Tools = tools ?? Array.Empty<AIToolDefinition>();
        Model = model ?? string.Empty;
        Temperature = temperature;
        MaxTokens = maxTokens;
    }
}
