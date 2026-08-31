using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.Automation;

public interface ITextSummarizerService
{
    Task<string> SummarizeAsync(string text, int maxWords = 200, CancellationToken ct = default);
    Task<string> ExtractKeyPointsAsync(string text, int maxPoints = 10, CancellationToken ct = default);
    Task<List<string>> ExtractKeywordsAsync(string text, int maxKeywords = 20, CancellationToken ct = default);
}

public sealed class TextSummarizerService : ITextSummarizerService
{
    private readonly ILogger<TextSummarizerService> _logger;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "le", "la", "les", "un", "une", "des", "de", "du", "au", "aux",
        "et", "ou", "mais", "donc", "car", "ni", "que", "qui", "quoi",
        "est", "sont", "été", "être", "avoir", "fait", "faire",
        "je", "tu", "il", "elle", "nous", "vous", "ils", "elles",
        "mon", "ton", "son", "notre", "votre", "leur",
        "ce", "cette", "ces", "ça", "celui", "celle",
        "dans", "sur", "sous", "avec", "pour", "par", "sans", "vers",
        "pas", "plus", "très", "trop", "aussi", "bien", "encore",
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by"
    };

    public TextSummarizerService(ILogger<TextSummarizerService> logger)
    {
        _logger = logger;
    }

    public Task<string> SummarizeAsync(string text, int maxWords = 200, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult("");

        var sentences = SplitSentences(text);
        var scored = sentences.Select(s => new { Sentence = s, Score = ScoreSentence(s) })
            .OrderByDescending(x => x.Score)
            .Take(sentences.Count / 3 + 1)
            .OrderBy(x => sentences.IndexOf(x.Sentence))
            .Select(x => x.Sentence);

        var summary = string.Join(" ", scored);
        var words = summary.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > maxWords)
            summary = string.Join(" ", words.Take(maxWords)) + "...";

        return Task.FromResult(summary);
    }

    public Task<string> ExtractKeyPointsAsync(string text, int maxPoints = 10, CancellationToken ct = default)
    {
        var sentences = SplitSentences(text);
        var scored = sentences.Select(s => new { Sentence = s.Trim(), Score = ScoreSentence(s) })
            .Where(x => x.Sentence.Length > 20)
            .OrderByDescending(x => x.Score)
            .Take(maxPoints)
            .Select((x, i) => $"{i + 1}. {x.Sentence}");

        return Task.FromResult(string.Join("\n", scored));
    }

    public Task<List<string>> ExtractKeywordsAsync(string text, int maxKeywords = 20, CancellationToken ct = default)
    {
        var words = text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);

        var keywords = words
            .Where(w => w.Length > 3 && !StopWords.Contains(w))
            .GroupBy(w => w.ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .Take(maxKeywords)
            .Select(g => g.Key)
            .ToList();

        return Task.FromResult(keywords);
    }

    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

        foreach (var c in text)
        {
            current.Append(c);
            if (c is '.' or '!' or '?' or '\n')
            {
                var s = current.ToString().Trim();
                if (s.Length > 10)
                    sentences.Add(s);
                current.Clear();
            }
        }

        if (current.Length > 10)
            sentences.Add(current.ToString().Trim());

        return sentences;
    }

    private static double ScoreSentence(string sentence)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double score = 0;

        // Position bonus (first sentences are more important)
        score += words.Length * 0.1;

        // Length bonus (medium length sentences are better)
        if (words.Length >= 10 && words.Length <= 30)
            score += 2;

        // Key phrase detection
        var keyPhases = new[] { "important", "key", "main", "primary", "significant", "crucial", "essential" };
        foreach (var phase in keyPhases)
        {
            if (sentence.Contains(phase, StringComparison.OrdinalIgnoreCase))
                score += 3;
        }

        return score;
    }
}
