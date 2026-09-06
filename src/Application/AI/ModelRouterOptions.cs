namespace JarvisAI.Application.AI;

public sealed record ModelRouterOptions(
    string FastModel = "llama3.1:latest",
    string ReasoningModel = "qwen3:8b",
    string? CodeModel = "qwen2.5-coder:7b",
    string? VisionModel = null,
    string? AgentModel = null,
    string KeepAlive = "30m",
    int LongConversationThreshold = 6,
    int NumCtx = 32768);

public enum ModelTier
{
    Fast,
    Reasoning,
    Code,
    Vision,
    Agent
}
