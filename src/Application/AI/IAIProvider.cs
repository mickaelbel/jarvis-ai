namespace JarvisAI.Application.AI;

public interface IAIProvider
{
    string Name { get; }
    bool IsAvailable { get; }
    IReadOnlyList<string> KnownModels { get; }
    bool MatchesModel(string? model);
    Task<AIResponse> ChatAsync(AIRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AIStreamChunk> StreamChatAsync(AIRequest request, CancellationToken cancellationToken = default);
}
