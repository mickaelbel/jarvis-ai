namespace JarvisAI.Application.AI;

public sealed record ModelRouterOptions(
    string FastModel = "qwen3:8b",
    string ReasoningModel = "qwen3:8b",
    string? CodeModel = null,
    string KeepAlive = "30m",
    int LongConversationThreshold = 6,
    int NumCtx = 32768);
