using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class RelevanceScorerTests
{
    [Fact]
    public void Exact_query_phrase_scores_higher()
    {
        var phrase = RelevanceScorer.Score("installer python windows", "Installer Python sur Windows", "");
        var partial = RelevanceScorer.Score("installer python windows", "Recette de cuisine", "");

        Assert.True(phrase > partial);
    }

    [Fact]
    public void Title_match_beats_snippet_only()
    {
        var title = RelevanceScorer.Score("ollama", "Ollama guide complet", "");
        var snippet = RelevanceScorer.Score("ollama", "autre", "contient ollama mentionné ici");

        Assert.True(title > snippet);
    }

    [Fact]
    public void Empty_query_scores_zero()
    {
        Assert.Equal(0.0, RelevanceScorer.Score("", "Titre", "snippet"));
    }

    [Fact]
    public void Ignore_stopword_queries()
    {
        Assert.Equal(0.0, RelevanceScorer.Score("le la les", "Titre quelconque", "snippet"));
    }

    [Fact]
    public void Score_is_clamped_between_zero_and_one()
    {
        var score = RelevanceScorer.Score("python", "Python Python Python", "python python python");

        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void No_overlap_scores_zero()
    {
        var score = RelevanceScorer.Score("django", "recette crepes sucres", "bien cuire");

        Assert.Equal(0.0, score);
    }
}

public sealed class LinkTypeClassifierTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123", "video")]
    [InlineData("https://youtu.be/abc123", "video")]
    [InlineData("https://www.youtube.com/shorts/xyz", "video")]
    [InlineData("https://vimeo.com/123456", "video")]
    public void Detects_video_urls(string url, string expected)
        => Assert.Equal(expected, LinkTypeClassifier.Detect(url));

    [Theory]
    [InlineData("https://example.com/guide.pdf", "pdf")]
    [InlineData("https://example.com/book.epub", "pdf")]
    [InlineData("https://example.com/file.pdf?dl=1", "pdf")]
    public void Detects_pdf_urls(string url, string expected)
        => Assert.Equal(expected, LinkTypeClassifier.Detect(url));

    [Theory]
    [InlineData("https://example.com/file.docx", "document")]
    [InlineData("https://example.com/slides.pptx", "presentation")]
    public void Detects_office_documents(string url, string expected)
        => Assert.Equal(expected, LinkTypeClassifier.Detect(url));

    [Theory]
    [InlineData("https://docs.python.org/3/tutorial/", "docs")]
    [InlineData("https://learn.microsoft.com/dotnet", "docs")]
    [InlineData("https://langchain.readthedocs.io/en/latest/", "docs")]
    [InlineData("https://reactjs.org/docs/hello.html", "docs")]
    [InlineData("https://kubernetes.io/api/v1", "docs")]
    public void Detects_docs_urls(string url, string expected)
        => Assert.Equal(expected, LinkTypeClassifier.Detect(url));

    [Theory]
    [InlineData("https://example.com/news/article", "article")]
    [InlineData("https://example.com/about", "article")]
    [InlineData("not a url", "article")]
    public void Defaults_to_article(string url, string expected)
        => Assert.Equal(expected, LinkTypeClassifier.Detect(url));
}
