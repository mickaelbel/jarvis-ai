using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace JarvisAI.Application.AI;

public sealed class CachedResponse
{
    public required string Key { get; init; }
    public required string Content { get; init; }
    public required string Model { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public sealed class ResponseCacheOptions
{
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxEntries { get; set; } = 500;
    public int MaxResponseBytes { get; set; } = 8000;
}

public interface IResponseCache
{
    CachedResponse? TryGet(string userMessage, string? model);
    void Set(string userMessage, string model, string content);
    void Clear();
    int PruneExpired();
    IReadOnlyList<CachedResponse> GetAll();
}

public sealed class InMemoryResponseCache : IResponseCache
{
    private static readonly HashSet<string> NonCacheableWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ouvre", "ouvrir", "lance", "lancer", "ferme", "stop", "arrete", "execute",
        "exécute", "cree", "crée", "installe", "supprime", "telecharge", "télécharge",
        "navigateur", "chrome", "terminal", "commande", "run", "open", "start",
        "supprimer", "delete", "deplace", "deplacer", "renomme", "envoie", "recherche"
    };

    private readonly ILogger<InMemoryResponseCache> _logger;
    private readonly ResponseCacheOptions _options;
    private readonly ConcurrentDictionary<string, CachedResponse> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _accessOrder = new();

    public InMemoryResponseCache(ILogger<InMemoryResponseCache> logger, ResponseCacheOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new ResponseCacheOptions();
    }

    public CachedResponse? TryGet(string userMessage, string? model)
    {
        if (!IsCacheable(userMessage)) return null;
        var key = BuildKey(userMessage, model);
        if (!_cache.TryGetValue(key, out var entry)) return null;
        if (DateTime.UtcNow - entry.CreatedUtc > _options.Ttl)
        {
            _cache.TryRemove(key, out _);
            return null;
        }
        _accessOrder.Enqueue(key);
        return entry;
    }

    public void Set(string userMessage, string model, string content)
    {
        if (!IsCacheable(userMessage)) return;
        if (string.IsNullOrWhiteSpace(content)) return;
        if (content.Length > _options.MaxResponseBytes) return;
        var key = BuildKey(userMessage, model);
        _cache[key] = new CachedResponse { Key = key, Content = content, Model = model, CreatedUtc = DateTime.UtcNow };
        _accessOrder.Enqueue(key);
        EvictIfOverCapacity();
    }

    public void Clear() => _cache.Clear();

    public int PruneExpired()
    {
        var now = DateTime.UtcNow;
        var removed = 0;
        foreach (var kv in _cache)
        {
            if (now - kv.Value.CreatedUtc > _options.Ttl && _cache.TryRemove(kv.Key, out _))
                removed++;
        }
        return removed;
    }

    public IReadOnlyList<CachedResponse> GetAll()
    {
        PruneExpired();
        return _cache.Values.OrderByDescending(e => e.CreatedUtc).ToArray();
    }

    private void EvictIfOverCapacity()
    {
        while (_cache.Count > _options.MaxEntries)
        {
            if (_accessOrder.TryDequeue(out var oldestKey))
                _cache.TryRemove(oldestKey, out _);
            else
                break;
        }
    }

    private static bool IsCacheable(string userMessage)
    {
        var text = (userMessage ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text) || text.Length < 8) return false;
        foreach (var word in NonCacheableWords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string BuildKey(string userMessage, string? model)
    {
        var normalized = new string(userMessage.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '\'').ToArray());
        var raw = model ?? "default";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized + "|" + raw));
        return Convert.ToHexString(hash);
    }
}
