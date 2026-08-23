namespace JarvisAI.Application.AI;

public sealed class AIResponse
{
    public bool Success { get; }
    public string Content { get; }
    public IReadOnlyList<AIToolCall> ToolCalls { get; }
    public string? ErrorMessage { get; }
    public string? Model { get; }
    public int PromptTokens { get; }
    public int CompletionTokens { get; }
    public long EvalDurationMs { get; }

    public double TokensPerSecond
        => EvalDurationMs > 0 ? (double)CompletionTokens / EvalDurationMs * 1000 : 0;

    private AIResponse(bool success, string content, IReadOnlyList<AIToolCall> toolCalls, string? errorMessage, string? model, int promptTokens, int completionTokens, long evalDurationMs)
    {
        Success = success;
        Content = content;
        ToolCalls = toolCalls;
        ErrorMessage = errorMessage;
        Model = model;
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        EvalDurationMs = evalDurationMs;
    }

    public static AIResponse Text(string content, string? model = null, int promptTokens = 0, int completionTokens = 0, long evalDurationMs = 0)
        => new(true, content, Array.Empty<AIToolCall>(), null, model, promptTokens, completionTokens, evalDurationMs);

    public static AIResponse WithToolCalls(IReadOnlyList<AIToolCall> toolCalls, string? model = null, int promptTokens = 0, int completionTokens = 0, long evalDurationMs = 0)
        => new(true, string.Empty, toolCalls, null, model, promptTokens, completionTokens, evalDurationMs);

    public static AIResponse Failed(string errorMessage)
        => new(false, string.Empty, Array.Empty<AIToolCall>(), errorMessage, null, 0, 0, 0);

    public bool HasToolCalls => ToolCalls.Count > 0;
}
