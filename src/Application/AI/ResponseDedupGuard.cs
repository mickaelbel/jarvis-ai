using System.Collections.Concurrent;

namespace JarvisAI.Application.AI;

public interface IResponseDedupGuard
{
    bool IsDuplicate(string response);
    void Record(string response);
}

public sealed class ResponseDedupGuard : IResponseDedupGuard
{
    private const int MaxHistory = 8;
    private const double SimilarityThreshold = 0.72;
    private readonly ConcurrentQueue<string> _recent = new();

    public bool IsDuplicate(string response)
    {
        var normalized = Normalize(response);
        if (normalized.Length < 30) return false;

        var tokens = Tokenize(normalized);
        if (tokens.Count < 4) return false;

        foreach (var past in _recent)
        {
            var pastTokens = Tokenize(Normalize(past));
            if (Jaccard(tokens, pastTokens) >= SimilarityThreshold)
                return true;
        }
        return false;
    }

    public void Record(string response)
    {
        _recent.Enqueue(response);
        while (_recent.Count > MaxHistory)
            _recent.TryDequeue(out _);
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        var union = new HashSet<string>(a);
        union.UnionWith(b);
        if (union.Count == 0) return 1.0;
        var intersection = a.Count(b.Contains);
        return (double)intersection / union.Count;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = part.Trim(' ', ',', '.', ';', ':', '!', '?', '\'', '"', '(', ')', '\n', '\t', '-', '—');
            if (cleaned.Length >= 3) tokens.Add(cleaned);
        }
        return tokens;
    }

    private static string Normalize(string text)
    {
        var lowered = text.ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lowered.Length);
        foreach (var c in lowered)
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '\'' or '\u2019')
                builder.Append(c);
            else
                builder.Append(' ');
        }
        return builder.ToString();
    }
}
