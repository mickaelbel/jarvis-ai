using JarvisAI.Application.AI;

namespace JarvisAI.Application.Security;

public interface IAIService
{
    const string StreamRestartMarker = "\u0002RESTART\u0002";
    Task<AIResponse> ChatAsync(string userMessage, AIConversation? conversation = null, string? model = null, ModelSelectionMode mode = ModelSelectionMode.Powerful, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamChatAsync(string userMessage, AIConversation? conversation = null, string? model = null, ModelSelectionMode mode = ModelSelectionMode.Powerful, CancellationToken cancellationToken = default);
    Task<string> BuildSystemPromptWithMemoryAsync(IReadOnlyList<AIToolDefinition> tools, CancellationToken cancellationToken = default);
}
