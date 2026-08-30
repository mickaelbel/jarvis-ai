using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

public interface ISentimentAnalysisService
{
    SentimentResult Analyze(string text);
    IReadOnlyList<SentimentResult> AnalyzeBatch(IEnumerable<string> texts);
    SentimentTrend GetTrend(int maxMessages = 50);
    string GetAdaptedTone(string text, SentimentResult sentiment);
}

public sealed class SentimentAnalysisService : ISentimentAnalysisService
{
    private readonly ILogger<SentimentAnalysisService> _logger;
    private readonly List<SentimentResult> _history = new();

    // French sentiment keywords
    private static readonly Dictionary<string, double> PositiveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["merci"] = 0.6, ["super"] = 0.8, ["génial"] = 0.9, ["excellent"] = 0.9,
        ["parfait"] = 1.0, ["bravo"] = 0.8, ["formidable"] = 0.9, ["magnifique"] = 0.9,
        ["fantastique"] = 1.0, ["incroyable"] = 0.9, ["adore"] = 0.8, ["aime"] = 0.6,
        ["bien"] = 0.4, ["ok"] = 0.2, ["d'accord"] = 0.3, ["superbe"] = 0.9,
        ["formidable"] = 0.9, ["merveilleux"] = 1.0, ["formidable"] = 0.9,
        ["heureux"] = 0.7, ["content"] = 0.6, ["satisfait"] = 0.7,
        ["réussi"] = 0.8, ["fonctionne"] = 0.5, ["parfaitement"] = 1.0
    };

    private static readonly Dictionary<string, double> NegativeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["erreur"] = -0.7, ["bug"] = -0.8, ["problème"] = -0.6, ["échec"] = -0.8,
        ["raté"] = -0.7, ["nul"] = -0.9, ["mauvais"] = -0.7, ["horrible"] = -0.9,
        ["terrible"] = -0.8, ["pire"] = -0.9, ["difficile"] = -0.4, ["compliqué"] = -0.4,
        ["lent"] = -0.5, ["freeze"] = -0.7, ["crash"] = -0.9, ["planté"] = -0.8,
        ["foiré"] = -0.9, ["cassé"] = -0.8, ["galère"] = -0.6, ["pénible"] = -0.6,
        ["agacé"] = -0.7, ["frustré"] = -0.8, ["énervé"] = -0.7, ["furieux"] = -0.9,
        ["déteste"] = -0.9, ["insupportable"] = -0.9, ["inutile"] = -0.7,
        ["lamentable"] = -0.8, ["naze"] = -0.9, ["nul"] = -0.8, ["pourri"] = -0.9
    };

    private static readonly Dictionary<string, double> Intensifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["très"] = 1.5, ["vraiment"] = 1.4, ["extrêmement"] = 1.8, ["absolument"] = 1.6,
        ["totalement"] = 1.5, ["complètement"] = 1.5, ["ultra"] = 1.6, ["super"] = 1.3,
        ["hyper"] = 1.4, ["tellement"] = 1.3, ["trop"] = 1.2, ["assez"] = 0.8,
        ["peu"] = 0.6, ["légèrement"] = 0.5, ["à peine"] = 0.4
    };

    public SentimentAnalysisService(ILogger<SentimentAnalysisService> logger)
    {
        _logger = logger;
    }

    public SentimentResult Analyze(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SentimentResult { Score = 0, Label = "neutral", Confidence = 0 };

        var words = text.Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '!', '?', ';', ':' },
            StringSplitOptions.RemoveEmptyEntries);

        double totalScore = 0;
        int sentimentWords = 0;
        double currentMultiplier = 1.0;

        foreach (var word in words)
        {
            if (Intensifiers.TryGetValue(word, out var intensity))
            {
                currentMultiplier = intensity;
                continue;
            }

            if (PositiveWords.TryGetValue(word, out var posScore))
            {
                totalScore += posScore * currentMultiplier;
                sentimentWords++;
                currentMultiplier = 1.0;
            }
            else if (NegativeWords.TryGetValue(word, out var negScore))
            {
                totalScore += negScore * currentMultiplier;
                sentimentWords++;
                currentMultiplier = 1.0;
            }
            else
            {
                currentMultiplier = 1.0;
            }
        }

        // Exclamation marks increase intensity
        int exclamationCount = text.Count(c => c == '!');
        totalScore *= 1 + (exclamationCount * 0.1);

        // Question marks can indicate confusion/frustration
        int questionCount = text.Count(c => c == '?');
        if (questionCount > 2)
            totalScore -= 0.2;

        double normalizedScore = Math.Clamp(totalScore / Math.Max(sentimentWords, 1), -1, 1);
        double confidence = Math.Min((double)sentimentWords / words.Length, 1.0);

        var label = normalizedScore switch
        {
            > 0.3 => "positive",
            < -0.3 => "negative",
            _ => "neutral"
        };

        var result = new SentimentResult
        {
            Score = normalizedScore,
            Label = label,
            Confidence = confidence,
            AnalyzedText = text,
            PositiveWords = words.Where(w => PositiveWords.ContainsKey(w)).ToList(),
            NegativeWords = words.Where(w => NegativeWords.ContainsKey(w)).ToList(),
            AnalyzedAt = DateTime.UtcNow
        };

        _history.Add(result);

        _logger.LogDebug("[Sentiment] Score: {Score:F2}, Label: {Label}, Confidence: {Confidence:P0}",
            result.Score, result.Label, result.Confidence);

        return result;
    }

    public IReadOnlyList<SentimentResult> AnalyzeBatch(IEnumerable<string> texts)
        => texts.Select(Analyze).ToList();

    public SentimentTrend GetTrend(int maxMessages = 50)
    {
        var recent = _history.TakeLast(maxMessages).ToList();
        if (recent.Count == 0)
            return new SentimentTrend { AverageScore = 0, Trend = "stable" };

        var avgScore = recent.Average(r => r.Score);
        var recentAvg = recent.TakeLast(5).Average(r => r.Score);
        var olderAvg = recent.Take(Math.Max(1, recent.Count - 5)).Average(r => r.Score);

        var trend = (recentAvg - olderAvg) switch
        {
            > 0.1 => "improving",
            < -0.1 => "declining",
            _ => "stable"
        };

        return new SentimentTrend
        {
            AverageScore = avgScore,
            Trend = trend,
            RecentAverage = recentAvg,
            SampleSize = recent.Count
        };
    }

    public string GetAdaptedTone(string text, SentimentResult sentiment)
    {
        if (sentiment.Label == "negative" && sentiment.Score < -0.5)
            return "empathetic";
        if (sentiment.Label == "positive" && sentiment.Score > 0.5)
            return "enthusiastic";
        return "neutral";
    }
}

public sealed class SentimentResult
{
    public double Score { get; set; }
    public string Label { get; set; } = "";
    public double Confidence { get; set; }
    public string AnalyzedText { get; set; } = "";
    public List<string> PositiveWords { get; set; } = new();
    public List<string> NegativeWords { get; set; } = new();
    public DateTime AnalyzedAt { get; set; }
}

public sealed class SentimentTrend
{
    public double AverageScore { get; set; }
    public string Trend { get; set; } = "";
    public double RecentAverage { get; set; }
    public int SampleSize { get; set; }
}
