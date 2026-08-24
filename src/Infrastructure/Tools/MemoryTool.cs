using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class MemoryTool : ITool
{
    private readonly IMemoryService _memoryService;
    private readonly ILogger<MemoryTool> _logger;

    public string Name => "memory";
    public string Description => "Mémoire long terme structurée. Actions : save (mémorise), get (relit par clé), search (recherche), delete (oublie). Catégories à utiliser pour save/search : 'préférence' (ce que l'utilisateur aime/n'aime pas, ses habitudes), 'personne' (ses proches : prénom → relation/détails), 'projet' (ses projets en cours), 'fait' (information durable sur sa vie). IMPORTANT : appelle memory action=save AUTOMATIQUEMENT et sans commenter dès que l'utilisateur exprime une préférence, mentionne un proche ou un projet — n'attends pas qu'il te le demande.";
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

    public MemoryTool(IMemoryService memoryService, ILogger<MemoryTool> logger)
    {
        _memoryService = memoryService;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);

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
        if (!parameters.TryGetValue("content", out var content) || string.IsNullOrWhiteSpace(content))
            return ToolResult.Failed("Parameter 'content' is required for save action.");

        // Clé optionnelle : générée depuis le contenu si absente.
        if (!parameters.TryGetValue("key", out var key) || string.IsNullOrWhiteSpace(key))
            key = DeriveKey(content);

        parameters.TryGetValue("category", out var category);

        // Upsert : une clé existante est remplacée au lieu de lever une erreur.
        try
        {
            if (await _memoryService.GetAsync(key, cancellationToken) is not null)
                await _memoryService.DeleteAsync(key, cancellationToken);

            var entry = await _memoryService.SaveAsync(
                key, content, MemoryType.Fact, category ?? "fait",
                cancellationToken: cancellationToken,
                importance: 0.9f);

            _logger.LogInformation("[MemoryTool] Saved: {Key} (Category: {Category})", key, category ?? "fait");
            return ToolResult.Succeeded($"Mémorisé durablement : {key} → {content}. ACTION TERMINÉE.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MemoryTool] Save failed for key {Key}, retrying as derived key", key);
            key = "memo-" + Guid.NewGuid().ToString("N")[..8];
            await _memoryService.SaveAsync(
                key, content, MemoryType.Fact, category ?? "fait",
                cancellationToken: cancellationToken,
                importance: 0.9f);
            return ToolResult.Succeeded($"Mémorisé durablement : {key} → {content}. ACTION TERMINÉE.");
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
