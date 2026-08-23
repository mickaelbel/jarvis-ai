using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class TopicExtractorTests
{
    [Fact]
    public void Extracts_meaningful_keywords_from_title()
    {
        var terms = TopicExtractor.Extract("L'iPhone 16 et ses nouvelles fonctionnalités incroyables");

        Assert.NotEmpty(terms);
        Assert.Contains("iphone", terms);
    }

    [Fact]
    public void Removes_french_stopwords()
    {
        var terms = TopicExtractor.Extract("le la les un une des pour avec");

        Assert.Empty(terms);
    }

    [Fact]
    public void Ignores_short_tokens()
    {
        var terms = TopicExtractor.Extract("ai ml c#");

        Assert.DoesNotContain(terms, t => t.Length < 4);
    }

    [Fact]
    public void Caps_number_of_terms()
    {
        var terms = TopicExtractor.Extract("alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo");

        Assert.True(terms.Count <= 4);
    }

    [Fact]
    public void Extract_from_all_combines_sources()
    {
        var result = new SearchResult
        {
            Title = "Python tutorial",
            Url = "https://example.com/1",
            Snippet = "python programming guide"
        };

        var terms = TopicExtractor.ExtractFromAll(new[] { result });

        Assert.Contains("python", terms);
    }
}

public sealed class TopicResultGrouperTests
{
    private readonly TopicResultGrouper _grouper = new();

    [Fact]
    public void Groups_results_sharing_topics()
    {
        var a = TestResults.Result("Tutoriel Python pour débutant", "https://a.com/1");
        var b = TestResults.Result("Tutoriel Python avancé complet", "https://b.com/2");
        a.Confidence = 0.5;
        b.Confidence = 0.5;

        var groups = _grouper.Group(new[] { a, b });

        Assert.Single(groups);
        Assert.Equal(a.GroupId, b.GroupId);
    }

    [Fact]
    public void Does_not_group_unrelated_results()
    {
        var a = TestResults.Result("Météo à Bordeaux", "https://a.com/1");
        var b = TestResults.Result("Recette de crêpes", "https://b.com/2");
        a.Confidence = 0.5;
        b.Confidence = 0.5;

        var groups = _grouper.Group(new[] { a, b });

        Assert.Equal(2, groups.Count);
        Assert.NotEqual(a.GroupId, b.GroupId);
    }

    [Fact]
    public void Assigns_distinct_group_ids()
    {
        var a = TestResults.Result("Soleil", "https://a.com/1");
        var b = TestResults.Result("Lune", "https://b.com/2");
        a.Confidence = 0.5;
        b.Confidence = 0.5;

        _grouper.Group(new[] { a, b });

        Assert.NotEqual(a.GroupId, b.GroupId);
    }

    [Fact]
    public void Single_result_gets_own_group()
    {
        var a = TestResults.Result("Unique sujet", "https://a.com/1");
        a.Confidence = 0.5;

        var groups = _grouper.Group(new[] { a });

        Assert.Single(groups);
        Assert.Equal("Unique sujet", groups[0].Topic);
    }
}

public sealed class NearDuplicateDetectorTests
{
    private readonly NearDuplicateDetector _detector = new();

    [Fact]
    public void Merges_results_with_identical_urls()
    {
        var a = TestResults.Result("A", "https://same.com/page");
        var b = TestResults.Result("B", "https://same.com/page");

        var merged = _detector.Merge(new[] { a, b });

        Assert.Single(merged);
    }

    [Fact]
    public void Merges_near_duplicate_titles()
    {
        var a = TestResults.Result("Comment installer Python sur Windows", "https://a.com/1");
        var b = TestResults.Result("Comment installer Python sur Windows", "https://b.com/2");

        var merged = _detector.Merge(new[] { a, b });

        Assert.Single(merged);
    }

    [Fact]
    public void Keeps_official_result_over_non_official()
    {
        var a = TestResults.Result("Titre commun ici", "https://mirror.example.com");
        var b = TestResults.Result("Titre commun ici", "https://samsung.com");
        b.IsOfficial = true;

        var merged = _detector.Merge(new[] { a, b });

        Assert.Single(merged);
        Assert.Equal("https://samsung.com", merged[0].Url);
    }

    [Fact]
    public void Keeps_higher_confidence_on_merge()
    {
        var a = TestResults.Result("Le modèle Gemini expliqué", "https://a.com/1");
        a.Confidence = 0.9;
        var b = TestResults.Result("Le modèle Gemini expliqué", "https://b.com/2");
        b.Confidence = 0.4;

        var merged = _detector.Merge(new[] { a, b });

        Assert.Equal("https://a.com/1", merged[0].Url);
    }

    [Fact]
    public void Increments_merged_count()
    {
        var a = TestResults.Result("Sujet partagé par deux sources", "https://a.com/1");
        var b = TestResults.Result("Sujet partagé par deux sources", "https://b.com/2");

        var merged = _detector.Merge(new[] { a, b });

        Assert.Equal(2, merged[0].MergedCount);
    }

    [Fact]
    public void Distinct_results_are_preserved()
    {
        var a = TestResults.Result("Premier sujet distinct", "https://a.com/1");
        var b = TestResults.Result("Second sujet différent", "https://b.com/2");

        var merged = _detector.Merge(new[] { a, b });

        Assert.Equal(2, merged.Count);
        Assert.All(merged, r => Assert.Equal(1, r.MergedCount));
    }
}
