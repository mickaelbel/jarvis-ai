using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class TopicExtractorExpandedTests
{
    [Theory]
    [InlineData("Learn Python programming", new[] { "learn", "python", "programming" })]
    [InlineData("The quick brown fox jumps", new[] { "quick", "brown", "jumps" })]
    [InlineData("Installer Python sur Windows", new[] { "installer", "python", "windows" })]
    [InlineData("Recette cuisine facile", new[] { "recette", "cuisine", "facile" })]
    [InlineData("Comment apprendre python", new[] { "comment", "apprendre", "python" })]
    [InlineData("a by in an to python", new[] { "python" })]
    public void Extracts_meaningful_words(string title, string[] expected)
    {
        Assert.Equal(expected, TopicExtractor.Extract(title));
    }

    [Theory]
    [InlineData("the and for with python", new[] { "python" })]
    [InlineData("le la les une python", new[] { "python" })]
    [InlineData("official video python", new[] { "python" })]
    [InlineData("the official announcement", new[] { "announcement" })]
    [InlineData("vidéo le dernier tuto", new[] { "tuto" })]
    public void Filters_stopwords(string title, string[] expected)
    {
        Assert.Equal(expected, TopicExtractor.Extract(title));
    }

    [Fact]
    public void Lowercases_tokens()
    {
        Assert.Equal(new[] { "python", "programming" }, TopicExtractor.Extract("PYTHON Programming"));
    }

    [Fact]
    public void Returns_max_four_terms()
    {
        var terms = TopicExtractor.Extract("alpha beta gamma delta epsilon zeta");
        Assert.Equal(4, terms.Count);
    }

    [Fact]
    public void Deduplicates_tokens()
    {
        Assert.Equal(new[] { "python" }, TopicExtractor.Extract("python python python"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_title_returns_empty(string title)
    {
        Assert.Empty(TopicExtractor.Extract(title));
    }

    [Fact]
    public void Null_title_returns_empty()
    {
        Assert.Empty(TopicExtractor.Extract(null!));
    }

    [Fact]
    public void ExtractFromAll_merges_across_results()
    {
        var results = new[]
        {
            TestResults.Result("Learn Python basics", "https://x.com/a"),
            TestResults.Result("python advanced tutorial", "https://x.com/b"),
            TestResults.Result("LEARN python", "https://x.com/c")
        };

        var terms = TopicExtractor.ExtractFromAll(results);

        Assert.Contains("learn", terms);
        Assert.Contains("python", terms);
        Assert.Contains("advanced", terms);
        Assert.Contains("tutorial", terms);
    }

    [Fact]
    public void ExtractFromAll_ignores_blank_titles()
    {
        var results = new[]
        {
            TestResults.Result("meaningful topic", "https://x.com/a"),
            TestResults.Result("", "https://x.com/b")
        };

        var terms = TopicExtractor.ExtractFromAll(results);

        Assert.Contains("meaningful", terms);
        Assert.Contains("topic", terms);
    }

    [Fact]
    public void ExtractFromAll_empty_input_returns_empty()
    {
        Assert.Empty(TopicExtractor.ExtractFromAll(Array.Empty<SearchResult>()));
    }
}

public sealed class TopicResultGrouperExpandedTests
{
    private readonly TopicResultGrouper _grouper = new();

    [Fact]
    public void Single_result_single_group()
    {
        var groups = _grouper.Group(new[] { TestResults.Result("Install Python", "https://x.com/a") });

        Assert.Single(groups);
        Assert.Equal(1, groups[0].Id);
        Assert.Equal("Install Python", groups[0].Topic);
        Assert.Single(groups[0].Members);
        Assert.Equal(1, groups[0].Members[0].GroupId);
    }

    [Fact]
    public void Overlapping_results_are_grouped()
    {
        var first = TestResults.Result("Install Python windows", "https://x.com/a");
        var second = TestResults.Result("Python windows setup", "https://x.com/b");

        var groups = _grouper.Group(new[] { first, second });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Members.Count);
        Assert.Equal(first.GroupId, second.GroupId);
    }

    [Fact]
    public void Single_word_overlap_is_not_grouped()
    {
        var groups = _grouper.Group(new[]
        {
            TestResults.Result("Python guide", "https://x.com/a"),
            TestResults.Result("Python tutorial", "https://x.com/b")
        });

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Results_without_terms_are_never_grouped()
    {
        var groups = _grouper.Group(new[]
        {
            TestResults.Result("abc def ghi", "https://x.com/a"),
            TestResults.Result("jkl mno pqr", "https://x.com/b")
        });

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Group_ids_increment()
    {
        var groups = _grouper.Group(new[]
        {
            TestResults.Result("Recette cuisine", "https://x.com/a"),
            TestResults.Result("Météo prévisions", "https://x.com/b"),
            TestResults.Result("Cinéma séance", "https://x.com/c")
        });

        Assert.Equal(new[] { 1, 2, 3 }, groups.Select(g => g.Id));
    }

    [Fact]
    public void Topic_comes_from_richer_title()
    {
        var groups = _grouper.Group(new[]
        {
            TestResults.Result("Python windows", "https://x.com/a"),
            TestResults.Result("Python windows setup install", "https://x.com/b")
        });

        Assert.Single(groups);
        Assert.Equal("Python windows setup install", groups[0].Topic);
    }

    [Fact]
    public void Empty_results_no_groups()
    {
        Assert.Empty(_grouper.Group(Array.Empty<SearchResult>()));
    }

    [Fact]
    public void Grouped_results_share_group_id()
    {
        var first = TestResults.Result("Cuisine facile recette", "https://x.com/a");
        var second = TestResults.Result("Cuisine facile dessert", "https://x.com/b");
        var unrelated = TestResults.Result("Météo demain ciel", "https://x.com/c");

        var groups = _grouper.Group(new[] { first, second, unrelated });

        Assert.Equal(2, groups.Count);
        Assert.Equal(first.GroupId, second.GroupId);
        Assert.NotEqual(first.GroupId, unrelated.GroupId);
    }
}
