using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace JarvisAI.Application.Memory;

public sealed record ScoredMemory(MemoryEntry Entry, float Score);

public sealed class MemorySemanticScorer
{
    private readonly IEmbeddingService? _embeddings;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, float[]> _embeddingCache = new();

    public MemorySemanticScorer(ILogger logger, IEmbeddingService? embeddings = null)
    {
        _logger = logger;
        _embeddings = embeddings;
    }

    public async Task<IReadOnlyList<ScoredMemory>> ScoreAsync(
        string query, IEnumerable<MemoryEntry> candidates, int top = 10,
        CancellationToken cancellationToken = default)
    {
        var entries = candidates as IReadOnlyList<MemoryEntry> ?? candidates.ToList();
        if (entries.Count == 0)
            return Array.Empty<ScoredMemory>();

        if (_embeddings is not null)
        {
            try
            {
                if (await _embeddings.IsAvailableAsync(cancellationToken))
                {
                    var queryEmbedding = await _embeddings.GenerateAsync(query, cancellationToken);
                    if (queryEmbedding is { Length: > 0 })
                    {
                        var scored = await Task.WhenAll(entries.Select(async entry =>
                        {
                            var embedding = await GetOrCreateEmbeddingAsync(entry, cancellationToken).ConfigureAwait(false);
                            var score = embedding is { Length: > 0 }
                                ? CosineSimilarity(queryEmbedding, embedding)
                                : LexicalScore(query, entry);
                            return new ScoredMemory(entry, score);
                        }));

                        return scored.OrderByDescending(x => x.Score).Take(top).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SemanticMemory] Embedding unavailable ({Model}), falling back to lexical scoring",
                    _embeddings.ModelName);
            }
        }

        var lexical = new List<ScoredMemory>(entries.Count);
        foreach (var entry in entries)
            lexical.Add(new ScoredMemory(entry, LexicalScore(query, entry)));

        return lexical.OrderByDescending(x => x.Score).Take(top).ToList();
    }

    private async Task<float[]?> GetOrCreateEmbeddingAsync(MemoryEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Embedding is { Length: > 0 })
            return entry.Embedding;

        if (_embeddingCache.TryGetValue(entry.Key, out var cached))
            return cached;

        if (_embeddings is null)
            return null;

        var embedding = await _embeddings.GenerateAsync(entry.Content, cancellationToken);
        if (embedding is { Length: > 0 })
            _embeddingCache[entry.Key] = embedding;

        return embedding;
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0f;

        float dot = 0f, normA = 0f, normB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= 0f || normB <= 0f)
            return 0f;

        return dot / (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    public static float LexicalScore(string query, MemoryEntry entry)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
            return entry.Importance * 0.5f;

        var haystack = Tokenize(entry.Content + " " + entry.Key + " " + entry.Category);
        var overlap = 0;
        foreach (var token in queryTokens)
        {
            if (haystack.Contains(token))
                overlap++;
        }

        var dice = (2f * overlap) / (queryTokens.Count + haystack.Count);

        var recencyBoost = 0f;
        var ageHours = (DateTime.UtcNow - entry.CreatedAt).TotalHours;
        if (ageHours < 168)
            recencyBoost = (float)(1 - ageHours / 168) * 0.2f;

        return dice + entry.Importance * 0.3f + recencyBoost;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        foreach (Match match in Regex.Matches(text, "[A-Za-zÀ-ÿ0-9]{2,}"))
        {
            if (!StopWords.Contains(match.Value))
                tokens.Add(match.Value);
        }

        return tokens;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "le", "la", "les", "de", "des", "du", "un", "une", "et", "ou", "dans", "pour",
        "avec", "sans", "sur", "en", "au", "aux", "que", "qui", "quoi", "dont", "par",
        "ne", "pas", "plus", "mais", "the", "a", "an", "and", "or", "of", "to", "in",
        "on", "for", "with", "from", "is", "are", "was", "be", "been", "it", "this",
        "that", "as", "at", "by", "not", "we", "you", "your", "my", "me", "he", "she",
        "they", "them", "can", "will", "would", "should", "could", "have", "has", "had",
        "do", "does", "did", "i", "so", "if", "then", "than", "too", "very", "just", "about"
    };
}
