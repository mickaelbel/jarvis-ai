namespace JarvisAI.Application.AI;

public interface IModelSelector
{
    Task<ModelRecommendation?> ClassifyAsync(string text, AIConversation? conversation = null, CancellationToken cancellationToken = default);
}
