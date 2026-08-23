namespace JarvisAI.Application.AI;

public sealed class AIMessage
{
    public AIMessageRole Role { get; }
    public string Content { get; }
    public string? ToolCallId { get; }
    public string? ToolCallName { get; }
    public IReadOnlyDictionary<string, string>? ToolCallArguments { get; }
    public IReadOnlyList<AIToolCall>? ToolCalls { get; }

    public AIMessage(
        AIMessageRole role,
        string content,
        string? toolCallId = null,
        string? toolCallName = null,
        IReadOnlyDictionary<string, string>? toolCallArguments = null,
        IReadOnlyList<AIToolCall>? toolCalls = null)
    {
        Role = role;
        Content = content;
        ToolCallId = toolCallId;
        ToolCallName = toolCallName;
        ToolCallArguments = toolCallArguments;
        ToolCalls = toolCalls;
    }

    public static AIMessage System(string content) => new(AIMessageRole.System, content);
    public static AIMessage User(string content) => new(AIMessageRole.User, content);
    public static AIMessage Assistant(string content) => new(AIMessageRole.Assistant, content);
    public static AIMessage AssistantWithToolCalls(string content, IReadOnlyList<AIToolCall> toolCalls)
        => new(AIMessageRole.Assistant, content, toolCalls: toolCalls);
    public static AIMessage Tool(string content, string toolCallId, string toolCallName)
        => new(AIMessageRole.Tool, content, toolCallId, toolCallName);
}
