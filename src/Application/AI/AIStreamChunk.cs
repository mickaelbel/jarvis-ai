namespace JarvisAI.Application.AI;

public sealed record AIStreamChunk(
    string? Token = null,
    IReadOnlyList<AIToolCall>? ToolCalls = null,
    string? Error = null,
    bool Done = false,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    long EvalDurationMs = 0);
