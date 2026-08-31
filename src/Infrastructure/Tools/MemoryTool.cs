using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class MemoryTool : ITool
{
    private readonly IMemoryService _memoryService;
    private readonly IAutomaticMemoryService _automaticMemory;
    private readonly ILogger<MemoryTool> _logger;

    public string Name => "memory";
    public string Description => "Mémoire long terme. Actions : save (mémorise), get (relit), search (recherche), delete (oublie). Catégories : 'préférence', 'personne', 'projet', 'fait'. IMPORTANT : content= doit ÊTRE COURT (max 50 mots). Exemples bons : 'Aime café sans sucre', 'Prénom Marie = épouse', 'Projet site web en cours'. MAUVAIS : longs paragraphes. Appelle memory save AUTOMATIQUEMENT quand l'utilisateur exprime une préférence.";
    public string Category => "memory";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Action: save, get, search, delete", typeof(string), required: true),
        new ToolParameter("key", "Clé courte et stable (pour save/get/delete). Ex: préférence='café', personne='marie', projet='site-web'", typeof(string)),
        new ToolParameter("content", "Contenu à mémoriser (pour save)", typeof(string)),
        new ToolParameter("search_text", "Texte recherché (pour search)", typeof(string)),
        new ToolParameter("category", "Catégorie : préférence, personne, projet, fait (pour save/search)", typeof(string))
    };

    public MemoryTool(IMemoryService memoryService, IAutomaticMemoryService automaticMemory, ILogger<MemoryTool> logger)
    {
        _memoryService = memoryService;
        _automaticMemory = automaticMemory;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);

        // Le modèle oublie parfois « action » : on la déduit des paramètres.
        if (string.IsNullOrWhiteSpace(action))
        {
            var hasContent = parameters.TryGetValue("content", out var c) && !string.IsNullOrWhiteSpace(c);
            var hasSearch = parameters.TryGetValue("search_text", out var s) && !string.IsNullOrWhiteSpace(s);
            if (hasContent) action = "save";
            else if (hasSearch) action = "search";
            else if (parameters.TryGetValue("key", out var k) && !string.IsNullOrWhiteSpace(k)) action = "get";
        }

        return action?.ToLowerInvariant() switch
        {
            "save" => await HandleSaveAsync(parameters, cancellationToken),
            "get" => await HandleGetAsync(parameters, cancellationToken),
            "search" => await HandleSearchAsync(parameters, cancellationToken),
            "delete" => await HandleDeleteAsync(parameters, cancellationToken),
            _ => ToolResult.Failed($"Unknown memory action: {action}. Use save, get, search, or delete.")
        };
    }

    private async Task<ToolResult> HandleSaveAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        if (!_automaticMemory.IsSavingEnabled())
            return ToolResult.Failed("Mémoire désactivée.");

        if (!parameters.TryGetValue("content", out var content) || string.IsNullOrWhiteSpace(content))
            return ToolResult.Failed("Parameter 'content' required.");

        // Limiter à 50 mots max
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 50)
            content = string.Join(" ", words.Take(50)) + "...";

        if (!parameters.TryGetValue("key", out var key) || string.IsNullOrWhiteSpace(key))
            key = DeriveKey(content);

        parameters.TryGetValue("category", out var category);

        // Upsert : une clé existante est remplacée au lieu de lever une erreur.
        try
        {
            if (await _memoryService.GetAsync(key, cancellationToken) is not null)
                await _memoryService.DeleteAsync(key, cancellationToken);

            // Triage intelligent : seul ce qui est réellement durable part en long terme ;
            // le reste va en court terme avec une expiration adaptée au contexte.
            var importance = Math.Max(4, _automaticMemory.ComputeImportance(content));
            var (tier, ttl) = _automaticMemory.DecideStorage(importance, content, MemoryType.Fact, string.IsNullOrWhiteSpace(category) ? "fait" : category);
            var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "fait" : category;

            var entry = await _memoryService.SaveMemoryAsync(
                key, content, MemoryType.Fact, normalizedCategory,
                tier: tier,
                ttl: ttl,
                importance: Math.Clamp(importance / 10f, 0f, 1f),
                metadata: _automaticMemory.IsDurable(content, MemoryType.Fact, normalizedCategory)
                    ? new Dictionary<string, string> { ["durable"] = "true" }
                    : new Dictionary<string, string> { ["durable"] = "false" },
                cancellationToken: cancellationToken);

            _logger.LogInformation("[MemoryTool] Saved: {Key} (Category: {Category}, Tier: {Tier})", key, normalizedCategory, tier);
            return ToolResult.Succeeded($"Mémorisé : {key} → {content} ({(tier == MemoryTier.LongTerm ? "long terme durable" : "court terme, expire le " + entry.ExpiresAt?.ToLocalTime().ToString("dd/MM/yyyy") + ")")}). ACTION TERMINÉE.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MemoryTool] Save failed for key {Key}, retrying as derived key", key);
            key = "memo-" + Guid.NewGuid().ToString("N")[..8];
            await _memoryService.SaveAsync(
                key, content, MemoryType.Fact, string.IsNullOrWhiteSpace(category) ? "fait" : category,
                cancellationToken: cancellationToken,
                importance: 0.9f);
            return ToolResult.Succeeded($"Mémorisé : {key} → {content}. ACTION TERMINÉE.");
        }
    }

    /// <summary>Clé stable et courte dérivée du contenu (slug).</summary>
    internal static string DeriveKey(string content)
    {
        var slug = new string(content.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(4);
        var key = string.Join("_", slug);
        return string.IsNullOrWhiteSpace(key) ? "memo-" + Guid.NewGuid().ToString("N")[..8] : key[..Math.Min(key.Length, 48)];
    }

    private async Task<ToolResult> HandleGetAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("key", out var key) || string.IsNullOrWhiteSpace(key))
            return ToolResult.Failed("Parameter 'key' is required for get action.");

        var entry = await _memoryService.GetAsync(key, cancellationToken);

        if (entry is null)
            return ToolResult.Succeeded($"No memory found for key: {key}");

        return ToolResult.Succeeded($"Memory '{entry.Key}': {entry.Content} (Type: {entry.Type}, Category: {entry.Category}, Created: {entry.CreatedAt:yyyy-MM-dd HH:mm:ss})");
    }

    private async Task<ToolResult> HandleSearchAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("search_text", out var text) || string.IsNullOrWhiteSpace(text))
            return ToolResult.Failed("Parameter 'search_text' is required for search action.");

        parameters.TryGetValue("category", out var category);

        var query = new MemoryQuery { TextSearch = text, Category = category };
        var results = await _memoryService.SearchAsync(query, cancellationToken);

        if (results.Count == 0)
            return ToolResult.Succeeded($"No memories found matching: {text}");

        var formatted = string.Join("\n", results.Select(r => $"- [{r.Key}] {r.Content} (Category: {r.Category}, Created: {r.CreatedAt:yyyy-MM-dd HH:mm:ss})"));
        return ToolResult.Succeeded($"Found {results.Count} memories:\n{formatted}");
    }

    private async Task<ToolResult> HandleDeleteAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("key", out var key) || string.IsNullOrWhiteSpace(key))
            return ToolResult.Failed("Parameter 'key' is required for delete action.");

        var deleted = await _memoryService.DeleteAsync(key, cancellationToken);
        return deleted
            ? ToolResult.Succeeded($"Memory deleted: {key}")
            : ToolResult.Succeeded($"No memory found to delete for key: {key}");
    }
}
