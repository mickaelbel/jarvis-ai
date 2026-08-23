namespace JarvisAI.Application.AI;

public sealed record ModelRecommendation(
    TaskCategory Category,
    string RecommendedModel,
    string Parameters,
    string DownloadSize,
    bool MultiStep,
    string Reason);
