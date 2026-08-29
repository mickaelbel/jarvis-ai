using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Application.AI;

/// <summary>
/// Condense automatiquement l'historique d'une conversation quand on approche de la
/// limite de tokens du modèle : l'IA produit un résumé structuré en mots-clés
/// (demandes, stade actuel, déjà fait, reste à faire, emplacements principaux) puis
/// la conversation est reconstruite avec ce résumé + les derniers messages bruts.
/// Le nouveau modèle d'une conversation récupère ainsi tout le contexte utile.
/// </summary>
public sealed class ConversationCondenser
{
    private const int ToolDefinitionsOverheadTokens = 1500;
    private const int MaxTokensPerSummary = 1500;

    private readonly IAIProvider _provider;
    private readonly ILogger<ConversationCondenser> _logger;
    private readonly int _maxContextTokens;
    private readonly int _triggerThresholdTokens;

    public ConversationCondenser(IAIProvider provider, ILogger<ConversationCondenser> logger, int numCtx = 32768)
    {
        _provider = provider;
        _logger = logger;
        _maxContextTokens = numCtx - 768;
        _triggerThresholdTokens = (int)(_maxContextTokens * 0.75);
    }

    /// <summary>Estimation grossière (chars / 4 + en-têtes) des tokens du contexte.</summary>
    public static int EstimateTokens(AIConversation conversation)
    {
        var tokens = (conversation.SystemPrompt.Length / 4) + ToolDefinitionsOverheadTokens;
        foreach (var msg in conversation.Messages)
        {
            tokens += (msg.Content.Length / 4) + 6;
            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var call in msg.ToolCalls)
                    tokens += (call.Name.Length / 4) + 12;
            }
        }
        return tokens;
    }

    public bool IsOverThreshold(AIConversation conversation)
        => conversation is not null && EstimateTokens(conversation) > _triggerThresholdTokens;

    /// <summary>
    /// Condense la conversation si elle dépasse le seuil de tokens. Retourne une
    /// nouvelle conversation (résumé + derniers messages bruts) ou null si rien à faire.
    /// </summary>
    public async Task<AIConversation?> CondenseIfNeededAsync(
        AIConversation conversation,
        string? model = null,
        int recentMessages = 6,
        CancellationToken cancellationToken = default)
    {
        if (conversation is null) return null;
        if (!IsOverThreshold(conversation)) return null;
        return await CondenseAsync(conversation, model, recentMessages, cancellationToken);
    }

    public async Task<AIConversation?> CondenseAsync(
        AIConversation conversation,
        string? model = null,
        int recentMessages = 6,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = conversation.ToRequestMessages();
            if (messages.Count <= recentMessages) return null;

            var historyToSummarize = messages.Take(messages.Count - recentMessages).ToList();
            var recent = messages.Skip(messages.Count - recentMessages).ToList();

            var instruction = new StringBuilder();
            instruction.AppendLine("Tu es Jarvis. Condense l'historique de conversation ci-dessous en un résumé structuré, en français, avec des MOTS-CLÉS et des points très concis. Réponds UNIQUEMENT avec le résumé, sans préambule.");
            instruction.AppendLine();
            instruction.AppendLine("Structure exacte à suivre :");
            instruction.AppendLine("- DEMANDES: les différentes demandes de l'utilisateur (mots-clés)");
            instruction.AppendLine("- STADE ACTUEL: à quel stade on en est");
            instruction.AppendLine("- FAIT: ce qui a déjà été fait");
            instruction.AppendLine("- RESTE: ce qui reste à faire");
            instruction.AppendLine("- EMPLACEMENTS: les emplacements principaux (fichiers/dossiers/chemins)");

            var request = new AIRequest(
                systemPrompt: instruction.ToString(),
                messages: historyToSummarize,
                tools: Array.Empty<AIToolDefinition>(),
                model: string.IsNullOrWhiteSpace(model) ? null : model,
                temperature: 0.2f,
                maxTokens: MaxTokensPerSummary);

            var response = await _provider.ChatAsync(request, cancellationToken);

            if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("[Condenser] Condensation failed: {Error}", response.ErrorMessage ?? "empty response");
                return null;
            }

            var summary = response.Content.Trim();
            _logger.LogInformation(
                "[Condenser] Conversation condensée ({HistoryCount} messages -> {SummaryChars} chars + {RecentCount} messages bruts conservés)",
                historyToSummarize.Count, summary.Length, recent.Count);

            var condensed = new AIConversation(conversation.SystemPrompt);
            condensed.AddMessage(AIMessage.System(
                "[Historique condensé de la conversation (sections : DEMANDES, STADE ACTUEL, FAIT, RESTE, EMPLACEMENTS)]\n"
                + summary));
            foreach (var msg in recent)
                condensed.AddMessage(msg);

            return condensed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Condenser] Condensation failed");
            return null;
        }
    }
}
