namespace JarvisAI.Application.AI;

public interface IModelRouter
{
    ModelRouterOptions Options { get; }
    ModelRouteResult Resolve(string? userMessage, AIConversation? conversation = null, ModelSelectionMode mode = ModelSelectionMode.Auto);
    IReadOnlyList<ModelRouteResult> RecentRoutes { get; }
    ModelRouteResult? LastRoute { get; }
}
