using JarvisAI.Application.Agents;
using JarvisAI.Application.Search;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class ContentExtractorTests
{
    private const string ArticleHtml = """
        <html>
        <head><title>L'IA en 2026 : le bilan</title></head>
        <body>
        <nav><a href="/menu">Menu</a></nav>
        <article>
          <h1>L'IA en 2026 : le bilan</h1>
          <p>L'intelligence artificielle a profondément transformé l'industrie du logiciel cette année.</p>
          <p>Les modèles open source ont gagné en popularité auprès des développeurs français.</p>
          <p>Acceptez les cookies pour continuer votre lecture.</p>
          <p>Court.</p>
          <p>Consultez notre guide complet pour comprendre les implications éthiques de ces technologies.</p>
          <a href="https://source-externe.example.org/rapport">Rapport officiel</a>
          <a href="https://autre-site.example.net/analyse">Analyse détaillée</a>
          <a href="#local">Lien interne</a>
          <a href="javascript:void(0)">Click</a>
        </article>
        <footer><a href="https://mentions.example.org">Mentions légales</a></footer>
        </body>
        </html>
        """;

    [Fact]
    public void Extracts_title()
    {
        var extracted = ContentExtractor.Extract(ArticleHtml);

        Assert.Equal("L'IA en 2026 : le bilan", extracted!.Title);
    }

    [Fact]
    public void Extracts_main_paragraphs()
    {
        var extracted = ContentExtractor.Extract(ArticleHtml);

        Assert.Contains("intelligence artificielle", extracted!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("modèles open source", extracted.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Filters_short_and_boilerplate_text()
    {
        var extracted = ContentExtractor.Extract(ArticleHtml);

        Assert.DoesNotContain("Acceptez les cookies", extracted!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Court.", extracted.Text);
        Assert.DoesNotContain("Menu", extracted.Text);
    }

    [Fact]
    public void Extracts_external_citations_only()
    {
        var extracted = ContentExtractor.Extract(ArticleHtml, url: "https://example.org/article");

        var citations = extracted!.Citations;
        Assert.Contains(citations, c => c.Contains("source-externe.example.org", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(citations, c => c.Contains("mentions.example.org", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(citations, c => c.Contains("#local", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Truncates_to_max_words()
    {
        var html = "<article>" + string.Concat(Enumerable.Range(0, 10).Select(i => $"<p>Le paragraphe numéro {i} contient plusieurs mots afin de dépasser la limite minimale de quarante caractères.</p>")) + "</article>";

        var extracted = ContentExtractor.Extract(html, maxWords: 15);

        Assert.True(extracted!.Text.Split(' ').Length <= 20);
    }

    [Fact]
    public void Uses_main_tag_fallback()
    {
        const string html = "<html><body><main><p>Un paragraphe suffisamment long pour être conservé dans le résultat final.</p></main></body></html>";

        var extracted = ContentExtractor.Extract(html);

        Assert.Contains("suffisamment long", extracted!.Text);
    }

    [Fact]
    public void Returns_null_for_garbage_input()
    {
        Assert.Null(ContentExtractor.Extract("<html>no content here</html>"));
    }

    [Fact]
    public void Returns_null_for_empty_input()
    {
        Assert.Null(ContentExtractor.Extract(""));
        Assert.Null(ContentExtractor.Extract(null));
    }

    [Fact]
    public void Uses_body_fallback_when_no_article_or_main()
    {
        const string html = "<html><head><title>T</title></head><body><p>Contenu de corps de page suffisamment long pour le test.</p></body></html>";

        var extracted = ContentExtractor.Extract(html);

        Assert.NotNull(extracted);
        Assert.Contains("Contenu de corps", extracted!.Text);
    }
}

public sealed class WebSearchToolExtractTests
{
    private sealed class StubContentService : IWebPageContentService
    {
        private readonly ContentExtractionResult? _result;
        public int Calls { get; private set; }

        public StubContentService(ContentExtractionResult? result) => _result = result;

        public Task<ContentExtractionResult?> ExtractAsync(string url, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private static WebSearchTool CreateTool(IWebPageContentService? content = null)
    {
        var service = new WebSearchService(
            Array.Empty<IWebSearchProvider>(),
            new FakeLinkVerifier(),
            new SearchCache(),
            new OfficialSiteDetector(),
            new FakeSiteDetector(),
            NullLogger<WebSearchService>.Instance);
        return new WebSearchTool(service, NullLogger<WebSearchTool>.Instance, contentService: content);
    }

    [Fact]
    public async Task Extract_returns_title_text_and_citations()
    {
        var stub = new StubContentService(new ContentExtractionResult(
            "https://example.org/a",
            "Mon article",
            "Voici le contenu principal de l'article.",
            new[] { "Source (https://source.example.org)" }));
        var tool = CreateTool(stub);

        var result = await tool.ExecuteAsync(new AgentContext("x"),
            new Dictionary<string, string> { ["action"] = "extract", ["url"] = "https://example.org/a" });

        Assert.True(result.Success);
        Assert.Contains("Mon article", result.Output);
        Assert.Contains("contenu principal", result.Output);
        Assert.Contains("source.example.org", result.Output);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task Extract_requires_url()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new AgentContext("x"),
            new Dictionary<string, string> { ["action"] = "extract" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Extract_failure_is_reported()
    {
        var stub = new StubContentService(null);
        var tool = CreateTool(stub);

        var result = await tool.ExecuteAsync(new AgentContext("x"),
            new Dictionary<string, string> { ["action"] = "extract", ["url"] = "https://example.org/missing" });

        Assert.False(result.Success);
    }
}
