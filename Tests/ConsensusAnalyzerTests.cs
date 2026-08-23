using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class ConsensusAnalyzerTests
{
    private readonly ConsensusAnalyzer _analyzer = new();

    [Fact]
    public void Empty_results_have_no_consensus()
    {
        var report = _analyzer.Analyze(Array.Empty<SearchResult>());

        Assert.False(report.HasConsensus);
        Assert.Equal(0, report.TotalSources);
    }

    [Fact]
    public void Multiple_sources_boost_agreement()
    {
        var results = new[]
        {
            TestResults.Result("iPhone 17 battery life review", "https://techcrunch.com/a", "news"),
            TestResults.Result("iPhone 17 battery life review", "https://theverge.com/b", "news"),
            TestResults.Result("iPhone 17 battery life review", "https://arstechnica.com/c", "news")
        };

        var report = _analyzer.Analyze(results);

        Assert.True(report.Agreement >= 0.7);
        Assert.Equal(3, report.DistinctDomains);
    }

    [Fact]
    public void Contradictory_numbers_are_reported()
    {
        var results = new[]
        {
            TestResults.Result("Prix 999€", "https://a.com", "bing"),
            TestResults.Result("Prix 1299€", "https://b.com", "duckduckgo")
        };

        var report = _analyzer.Analyze(results);

        Assert.True(report.Contradictions.Count > 0);
        Assert.False(report.HasConsensus);
    }

    [Fact]
    public void Single_source_has_moderate_agreement()
    {
        var results = new[] { TestResults.Result("Sole source", "https://a.com", "bing") };

        var report = _analyzer.Analyze(results);

        Assert.Equal(1, report.TotalSources);
        Assert.False(report.HasConsensus);
    }

    [Fact]
    public void Identifies_top_topic()
    {
        var results = new[]
        {
            TestResults.Result("Nouveau modèle ChatGPT 5 annoncé", "https://a.com", "news"),
            TestResults.Result("ChatGPT 5 disponible", "https://b.com", "news")
        };

        var report = _analyzer.Analyze(results);

        Assert.NotNull(report.TopTopic);
    }
}
