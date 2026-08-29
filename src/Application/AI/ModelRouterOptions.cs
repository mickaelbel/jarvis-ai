namespace JarvisAI.Application.AI;

public sealed record ModelRouterOptions(
    string FastModel = "qwen3.5:2b",
    string ReasoningModel = "llama3.1",
    string? CodeModel = null,
    string KeepAlive = "30m",
    int LongConversationThreshold = 6,
    int NumCtx = 32768);
