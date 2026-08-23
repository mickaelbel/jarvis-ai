using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class ConsensusExpandedTests
{
    private readonly ConsensusAnalyzer _analyzer = new();

    private static SearchResult R(string title, string url, string provider, string snippet = "snippet")
        => new()
        {
            Title = title,
            Url = url,
            Provider = provider,
            Snippet = snippet
        };

    [Fact]
    public void Empty_results_no_consensus()
    {
        var report = _analyzer.Analyze(Array.Empty<SearchResult>());
        Assert.Equal(0.0, report.Agreement);
        Assert.Equal(0, report.TotalSources);
        Assert.Equal(0, report.DistinctDomains);
        Assert.False(report.HasConsensus);
    }

    [Fact]
    public void Single_source_has_no_consensus()
    {
        var report = _analyzer.Analyze(new[] { R("Premier sujet", "https://x.com/a", "duckduckgo") });
        Assert.Equal(1, report.TotalSources);
        Assert.False(report.HasConsensus);
    }

    [Fact]
    public void Two_distinct_providers_reach_consensus()
    {
        var report = _analyzer.Analyze(new[]
        {
            R("Premier sujet", "https://x.com/a", "duckduckgo"),
            R("Autre thème", "https://y.com/b", "bing")
        });
        Assert.True(report.HasConsensus);
        Assert.True(report.Agreement >= 0.6);
    }

    [Fact]
    public void Same_provider_has_no_consensus()
    {
        var report = _analyzer.Analyze(new[]
        {
            R("Premier sujet", "https://x.com/a", "duckduckgo"),
            R("Autre thème", "https://y.com/b", "duckduckgo")
        });
        Assert.False(report.HasConsensus);
        Assert.Equal(0.5, report.Agreement, precision: 2);
    }

    [Fact]
    public void Repeated_claim_is_flagged_as_contradiction()
    {
        var report = _analyzer.Analyze(new[]
        {
            R("Prix maison", "https://x.com/m", "duckduckgo", "maison vendue 100 €"),
            R("Prix voiture", "https://y.com/v", "bing", "voiture vendue 100 €")
        });
        Assert.False(report.HasConsensus);
        Assert.Contains(report.Contradictions, c => c.Contains("répétée", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Conflicting_values_are_flagged()
    {
        var report = _analyzer.Analyze(new[]
        {
            R("Résultat enquête", "https://x.com/m", "duckduckgo", "note 8 sur 10"),
            R("Résultat sondage", "https://y.com/v", "bing", "note 5 sur 10")
        });
        Assert.Contains(report.Contradictions, c => c.Contains("répétée", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Contradictions, c => c.Contains("contradictoire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Distinct_domains_are_counted()
    {
        var report = _analyzer.Analyze(new[]
        {
            R("Un", "https://x.com/a", "duckduckgo"),
            R("Deux", "https://x.com/b", "bing"),
            R("Trois", "https://y.com/c", "news")
        });
        Assert.Equal(2, report.DistinctDomains);
        Assert.Equal(3, report.TotalSources);
    }

    [Fact]
    public void Dominant_topic_is_reported()
    {
        var report = _analyzer.Analyze(new[]
        {
            R("Python tutoriel débutant", "https://x.com/a", "duckduckgo"),
            R("Python tutoriel avancé", "https://y.com/b", "bing"),
            R("Python tutoriel astuces", "https://z.com/c", "news")
        });
        Assert.Equal("python", report.TopTopic);
        Assert.True(report.Agreement >= 0.8);
    }

    [Fact]
    public void Providers_count_towards_agreement()
    {
        var report = _analyzer.Analyze(new[]
        {
            R("Premier sujet", "https://x.com/a", "duckduckgo"),
            R("Autre thème", "https://y.com/b", "bing"),
            R("Troisième idée", "https://z.com/c", "news")
        });
        Assert.True(report.Agreement >= 0.6);
    }
}
