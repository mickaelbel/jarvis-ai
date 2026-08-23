using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class GitHubAdvancedTests
{
    private const string Json = """
        {"total_count":1,"items":[
          {"full_name":"ollama/ollama","html_url":"https://github.com/ollama/ollama","description":"Get up and running with Llama","stargazers_count":95000,"owner":{"login":"ollama"},"language":"Go","updated_at":"2026-01-01T00:00:00Z","fork":false}
        ]}
        """;

    [Fact]
    public async Task Official_repo_query_uses_repo_filter_url()
    {
        var handler = StubHttpHandler.For(Json);
        var provider = new GitHubSearchProvider(new HttpClient(handler), NullLogger<GitHubSearchProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "ollama", Type = SearchResultType.Repository });

        Assert.Single(results);
        Assert.Contains("q=repo%3Aollama%2Follama", handler.Requests[0].RequestUri!.ToString());
        Assert.True(results[0].IsOfficial);
        Assert.Equal(1.0, results[0].ExtraScore);
    }

    [Fact]
    public async Task Non_official_query_uses_standard_search()
    {
        var handler = StubHttpHandler.For(Json);
        var provider = new GitHubSearchProvider(new HttpClient(handler), NullLogger<GitHubSearchProvider>.Instance);

        await provider.SearchAsync(new SearchRequest { Query = "monorepo tooling", Type = SearchResultType.Repository });

        Assert.Contains("q=monorepo", handler.Requests[0].RequestUri!.ToString());
        Assert.DoesNotContain("repo%3A", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public void Official_matching_repo_is_marked_when_parsed_directly()
    {
        var results = GitHubSearchProvider.ParseResults(Json, 10, officialRepo: "ollama/ollama");

        Assert.True(results[0].IsOfficial);
        Assert.Equal(1.0, results[0].ExtraScore);
    }

    [Fact]
    public void Non_matching_repo_is_not_official()
    {
        var results = GitHubSearchProvider.ParseResults(Json, 10, officialRepo: "somewhere/else");

        Assert.False(results[0].IsOfficial);
    }

    [Fact]
    public void Empty_items_yields_empty_results()
    {
        var results = GitHubSearchProvider.ParseResults("""{"total_count":0,"items":[]}""", 10);

        Assert.Empty(results);
    }
}

public sealed class AcademicProviderTests
{
    [Fact]
    public void Semantic_scholar_parses_papers()
    {
        const string json = """
            {"data":[
              {"title":"Deep Learning for NLP","url":"https://www.semanticscholar.org/paper/ABC","abstract":"A paper about deep learning.",
               "publicationDate":"2025-03-01","authors":[{"name":"Alice A"},{"name":"Bob B"}],"venue":"ACL","citationCount":120,"openAccessPdf":{"url":"https://arxiv.org/pdf/1234.5678.pdf"}}
            ]}
            """;

        var results = SemanticScholarSearchProvider.ParseResults(json, 10);

        var paper = Assert.Single(results);
        Assert.Equal("Deep Learning for NLP", paper.Title);
        Assert.Equal("https://arxiv.org/pdf/1234.5678.pdf", paper.Url);
        Assert.Equal("Alice A, Bob B", paper.Author);
        Assert.NotNull(paper.PublishedAt);
        Assert.Contains("120 citations", paper.Snippet);
    }

    [Fact]
    public void Semantic_scholar_handles_empty_result()
    {
        var results = SemanticScholarSearchProvider.ParseResults("""{"total":0,"data":[]}""", 10);

        Assert.Empty(results);
    }

    [Fact]
    public void Hal_parses_documents()
    {
        const string json = """
            {"response":{"docs":[
              {"uri_s":"https://hal.science/hal-01234567","title_s":"Intelligence artificielle et éthique",
               "abstract_s":"Une étude sur l'éthique de l'IA.","authFullName_s":["Marie D","Paul E"],
               "producedDate_s":"2024-06-01T00:00:00Z","docType_s":"ART"}
            ]}}
            """;

        var results = HalSearchProvider.ParseResults(json, 10);

        var paper = Assert.Single(results);
        Assert.Equal("Intelligence artificielle et éthique", paper.Title);
        Assert.Equal("https://hal.science/hal-01234567", paper.Url);
        Assert.Equal("Marie D, Paul E", paper.Author);
        Assert.NotNull(paper.PublishedAt);
    }

    [Fact]
    public void Hal_handles_abstract_array()
    {
        const string json = """
            {"response":{"docs":[
              {"uri_s":"https://hal.science/hal-00000001","title_s":"Titre","abstract_s":["Première","Deuxième"],"docType_s":"ART"}
            ]}}
            """;

        var results = HalSearchProvider.ParseResults(json, 10);

        Assert.Single(results);
    }

    [Fact]
    public void Pubmed_parses_esearch_idlist()
    {
        const string json = """{"esearchresult":{"count":2,"idlist":["12345","67890"]}}""";

        var ids = PubMedSearchProvider.ParseEsearch(json);

        Assert.Equal(new[] { "12345", "67890" }, ids);
    }

    [Fact]
    public void Pubmed_parses_esummary_into_results()
    {
        const string json = """
            {"result":{"uids":["12345"],
              "12345":{"title":"A study on AI","fulljournalname":"Nature","pubdate":"2025 Jan 10",
                "authors":[{"name":"Smith J"},{"name":"Doe A"}],
                "articleids":[{"idtype":"doi","valuetype":"","value":"10.1000/abc"},{"idtype":"pubmed","valuetype":"","value":"12345"}]}}}
            """;

        var results = PubMedSearchProvider.ParseEsummary(json, 10);

        var paper = Assert.Single(results);
        Assert.Equal("A study on AI", paper.Title);
        Assert.Equal("https://doi.org/10.1000/abc", paper.Url);
        Assert.Equal("Smith J, Doe A", paper.Author);
        Assert.NotNull(paper.PublishedAt);
    }

    [Fact]
    public void Pubmed_falls_back_to_pubmed_url_without_doi()
    {
        const string json = """
            {"result":{"uids":["9"],
              "9":{"title":"Paper","articleids":[{"idtype":"pubmed","valuetype":"","value":"9"}]}}}
            """;

        var results = PubMedSearchProvider.ParseEsummary(json, 10);

        Assert.Equal("https://pubmed.ncbi.nlm.nih.gov/9/", results[0].Url);
    }

    [Fact]
    public void Pubmed_parses_relative_pubdates()
    {
        Assert.NotNull(PubMedSearchProvider.ParsePubDate("2024 Mar 5"));
        Assert.Null(PubMedSearchProvider.ParsePubDate(null));
    }

    [Fact]
    public void Crossref_parses_works()
    {
        const string json = """
            {"message":{"items":[
              {"title":["Attention is all you need"],"DOI":"10.5555/123",
               "abstract":"<jats:p>We propose a new architecture.</jats:p>",
               "author":[{"given":"Ashish","family":"Vaswani"}],
               "issued":{"date-parts":[[2017,6,12]]},
               "container-title":["NeurIPS"],"is-referenced-by-count":5000}
            ]}}
            """;

        var results = CrossRefSearchProvider.ParseResults(json, 10);

        var paper = Assert.Single(results);
        Assert.Equal("Attention is all you need", paper.Title);
        Assert.Equal("https://doi.org/10.5555/123", paper.Url);
        Assert.Equal("Ashish Vaswani", paper.Author);
        Assert.Contains("NeurIPS", paper.Snippet);
        Assert.NotNull(paper.PublishedAt);
        Assert.Contains("5000 citations", paper.Snippet);
    }

    [Fact]
    public void Crossref_strips_html_tags_from_abstract()
    {
        const string json = """
            {"message":{"items":[
              {"title":["X"],"DOI":"10.1/x","abstract":"<jats:p>Hello <i>world</i> here.</jats:p>"}
            ]}}
            """;

        var results = CrossRefSearchProvider.ParseResults(json, 10);

        Assert.Contains("Hello world here", results[0].Snippet);
    }
}
