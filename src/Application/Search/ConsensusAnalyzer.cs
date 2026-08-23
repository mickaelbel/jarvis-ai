using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public sealed class ConsensusReport
{
    public double Agreement { get; init; }
    public int TotalSources { get; init; }
    public int DistinctDomains { get; init; }
    public IReadOnlyList<string> Contradictions { get; init; } = Array.Empty<string>();
    public string? TopTopic { get; init; }
    public bool HasConsensus { get; init; }
}

public sealed class ConsensusAnalyzer
{
    private static readonly Regex NumberPattern = new(@"\b\d+(?:[.,]\d+)?\s*(?:€|\$|%|ans|m\b|km|ms|mo|go|g\b|gb|tb)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WordSplit = new(@"\W+", RegexOptions.Compiled);
    private static readonly Regex DigitCheck = new(@"\d", RegexOptions.Compiled);

    public ConsensusReport Analyze(IEnumerable<SearchResult> results)
    {
        var list = results.Where(r => !string.IsNullOrWhiteSpace(r.Title)).ToList();
        if (list.Count == 0)
            return new ConsensusReport { Agreement = 0, TotalSources = 0, HasConsensus = false };

        var domains = list.Select(r => SearchResultDeduper.CanonicalDomain(r.Url))
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var topics = list.SelectMany(r => TopicTerms(r.Title)).ToList();
        var topicCounts = topics.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        var distinctProviders = list.Select(r => r.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var agreement = distinctProviders >= 2 ? 0.7 : 0.5;
        if (topicCounts.Count > 0 && (double)topicCounts[0].Count() / list.Count >= 0.6)
            agreement = Math.Min(0.95, agreement + 0.2);

        var contradictions = FindContradictions(list);

        return new ConsensusReport
        {
            Agreement = Math.Clamp(agreement, 0.0, 1.0),
            TotalSources = list.Count,
            DistinctDomains = domains.Count,
            Contradictions = contradictions,
            TopTopic = topicCounts.Count > 0 ? topicCounts[0].Key : null,
            HasConsensus = list.Count >= 2 && agreement >= 0.6 && contradictions.Count == 0
        };
    }

    private static IReadOnlyList<string> FindContradictions(IReadOnlyList<SearchResult> results)
    {
        var contradictions = new List<string>();
        var sourcesByClaim = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var valuesByTopic = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in results)
        {
            var domain = SearchResultDeduper.CanonicalDomain(r.Url);
            var numbers = ExtractNumbers(r);

            foreach (var n in numbers)
            {
                if (!sourcesByClaim.TryGetValue(n, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    sourcesByClaim[n] = set;
                }
                set.Add(domain);

                foreach (var topic in TopicTerms(r.Title))
                {
                    if (!valuesByTopic.TryGetValue(topic, out var values))
                    {
                        values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        valuesByTopic[topic] = values;
                    }
                    values.Add(n);
                }
            }
        }

        foreach (var (claim, domains) in sourcesByClaim.Where(kv => kv.Value.Count >= 2).Take(3))
        {
            contradictions.Add($"Valeur '{claim}' répétée sur {domains.Count} domaines différents.");
        }

        foreach (var (topic, values) in valuesByTopic.Where(kv => kv.Value.Count >= 2).Take(3))
        {
            contradictions.Add($"Valeur contradictoire pour '{topic}': {string.Join(", ", values.OrderBy(v => v))}.");
        }

        return contradictions;
    }

    private static List<string> ExtractNumbers(SearchResult result)
    {
        var values = NumberPattern.Matches(result.Snippet + " " + result.Title)
            .Cast<Match>()
            .Select(m => m.Value.Trim())
            .Where(v => v.Length <= 12)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var significant = values.Where(v => v.Length >= 2 || DigitCheck.IsMatch(v)).ToList();
        return significant;
    }

    private static IEnumerable<string> TopicTerms(string title)
    {
        var words = WordSplit.Split(title)
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .Distinct(StringComparer.Ordinal);
        return words.Take(4);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "your", "you", "are",
        "not", "but", "has", "have", "was", "were", "all", "can", "will", "just",
        "le", "la", "les", "des", "une", "un", "pour", "avec", "dans", "sur",
        "d'un", "d'une", "plus", "que", "qui", "est", "sont", "dans", "cette"
    };
}
