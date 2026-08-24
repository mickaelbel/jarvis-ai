using JarvisAI.Application.Memory;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace JarvisAI.Application.Memory;

/// <summary>
/// Mémoire épisodique : chaque échange (question -> réponse) devient un
/// souvenir horodaté ; les plus pertinents sont rappelés dans le contexte des
/// conversations suivantes (chat et voix). "Qu'est-ce qu'on a dit hier ?"
/// fonctionne sans que l'utilisateur ait rien à mémoriser manuellement.
/// </summary>
public interface IEpisodicMemoryService
{
    /// <summary>Bloc de souvenirs pertinents à injecter avant la question ("" si aucun).</summary>
    Task<string> RecallBlockAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Enregistre un épisode (échange complet) comme souvenir.</summary>
    Task RecordAsync(string question, string answer, string source, CancellationToken cancellationToken = default);
}

public sealed class EpisodicMemoryService : IEpisodicMemoryService
{
    private readonly IMemoryService _memory;
    private readonly ILogger<EpisodicMemoryService> _logger;

    private static readonly TimeSpan EpisodeWindow = TimeSpan.FromDays(30);
    private const int MaxEpisodes = 5;
    private const int MaxQuestionLen = 400;
    private const int MaxAnswerLen = 600;
    private const string Category = "episodic";
    private const string CategoryPreferences = "preferences";
    private const int MaxPreferences = 8;

    /// <summary>
    /// Fournisseur optionnel de contexte visuel (ex. application au premier plan),
    /// injecté par la couche hôte — l'Application reste agnostique de l'OS.
    /// </summary>
    private readonly Func<string?>? _contextProbe;

    public EpisodicMemoryService(
        IMemoryService memory,
        ILogger<EpisodicMemoryService>? logger = null,
        Func<string?>? contextProbe = null)
    {
        _memory = memory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EpisodicMemoryService>.Instance;
        _contextProbe = contextProbe;
    }

    public async Task<string> RecallBlockAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";

        IReadOnlyList<MemoryEntry> episodes;
        try
        {
            // Recherche sémantique si les embeddings sont disponibles,
            // sinon repli sur la recherche texte du store.
            episodes = await _memory.SearchSemanticAsync(query, MaxEpisodes, category: Category, cancellationToken: cancellationToken);
            if (episodes.Count == 0)
                episodes = await _memory.SearchAsync(
                    new MemoryQuery { Category = Category, TextSearch = query.Trim(), Limit = MaxEpisodes },
                    cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Episodic] Rappel impossible (store indisponible ?)");
            return "";
        }

        var cutoff = DateTime.UtcNow - EpisodeWindow;
        var blocSouvenirs = "";
        var lines = episodes
            .Where(e => e.CreatedAt >= cutoff)
            .Take(MaxEpisodes)
            .Select(e =>
            {
                var local = e.CreatedAt.ToLocalTime();
                var stamp = local.Date == DateTime.Today
                    ? $"aujourd'hui à {local:HH:mm}"
                    : local.Date == DateTime.Today.AddDays(-1)
                        ? $"hier à {local:HH:mm}"
                        : $"le {local:dd/MM} à {local:HH:mm}";
                return $"• ({stamp}) {Truncate(e.Content, 320)}";
            })
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count > 0)
        {
            blocSouvenirs = string.Join("\n", lines);
        }

        // Mémoire procédurale : préférences et corrections apprises, sans expiration.
        try
        {
            var prefs = await _memory.SearchAsync(
                new MemoryQuery { Category = CategoryPreferences, Limit = MaxPreferences }, cancellationToken);
            var prefLines = prefs
                .Where(e => e.Category == CategoryPreferences)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => $"• {Truncate(e.Content, 200)}")
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (prefLines.Count > 0)
            {
                if (blocSouvenirs.Length > 0) blocSouvenirs += "\n\n";
                blocSouvenirs += "[Préférences et consignes permanentes apprises de cet utilisateur — applique-les systématiquement]\n"
                                 + string.Join("\n", prefLines);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Episodic] préférences indisponibles");
        }

        // RAG fichiers : extraits de documents indexés pertinents pour la question.
        try
        {
            IReadOnlyList<MemoryEntry> docs;
            try
            {
                docs = await _memory.SearchSemanticAsync(query, 3, category: "fichiers", cancellationToken: cancellationToken);
            }
            catch
            {
                docs = await _memory.SearchAsync(
                    new MemoryQuery { Category = "fichiers", TextSearch = query.Trim(), Limit = 3 }, cancellationToken);
            }
            var docLines = docs
                .Where(e => e.Category == "fichiers")
                .Select(e => $"• [{Truncate(e.Content, 260)}]")
                .ToList();
            if (docLines.Count > 0)
            {
                if (blocSouvenirs.Length > 0) blocSouvenirs += "\n\n";
                blocSouvenirs += "[Extraits des documents indexés localement — source fiable sur leur contenu]\n"
                                 + string.Join("\n", docLines);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Episodic] RAG fichiers indisponible");
        }

        if (blocSouvenirs.Length == 0) return "";

        return "[Souvenirs d'échanges passés avec cet utilisateur — utiles si pertinents, ne les cite pas mot pour mot]\n"
               + blocSouvenirs;
    }

    public async Task RecordAsync(string question, string answer, string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer)) return;
        if (answer.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var content = Truncate(question.ReplaceLineEndings(" "), MaxQuestionLen) + "\n→ "
                          + Truncate(answer.ReplaceLineEndings(" "), MaxAnswerLen);

            // Contexte visuel du moment (app au premier plan) si disponible.
            string? app = null;
            try { app = _contextProbe?.Invoke(); } catch { /* jamais bloquant */ }
            var metadata = new Dictionary<string, string> { ["source"] = source };
            if (!string.IsNullOrWhiteSpace(app))
            {
                content += $"\n[contexte : {app}]";
                metadata["app"] = app;
            }

            var key = $"episodic.{DateTime.UtcNow:yyyyMMdd.HHmmss}.{ShortHash(content)}";
            await _memory.SaveMemoryAsync(
                key,
                content,
                MemoryType.Conversation,
                Category,
                importance: 0.45f,
                tier: MemoryTier.ShortTerm,
                ttl: EpisodeWindow,
                metadata: metadata,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // L'enregistrement d'épisode ne doit jamais casser une conversation.
            _logger.LogDebug(ex, "[Episodic] Enregistrement impossible");
        }
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
