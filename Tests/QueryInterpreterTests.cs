using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class QueryInterpreterTests
{
    private readonly QueryInterpreter _interpreter = new();

    [Theory]
    [InlineData("la dernière vidéo de MrBeast", SearchResultType.Video)]
    [InlineData("video youtube recette crepes", SearchResultType.Video)]
    [InlineData("la chaîne de MrBeast", SearchResultType.Channel)]
    [InlineData("chaîne youtube de Linus", SearchResultType.Channel)]
    [InlineData("playlist musique détente", SearchResultType.Playlist)]
    [InlineData("match en direct", SearchResultType.Live)]
    [InlineData("youtube live concert", SearchResultType.Live)]
    [InlineData("shorts drôles", SearchResultType.Short)]
    [InlineData("le github d'Ollama", SearchResultType.Repository)]
    [InlineData("repository nodejs sur github", SearchResultType.Repository)]
    [InlineData("actualité tech du jour", SearchResultType.News)]
    [InlineData("dernières nouvelles France", SearchResultType.News)]
    [InlineData("arxiv paper sur les transformers", SearchResultType.Academic)]
    [InlineData("étude scientifique mémoire", SearchResultType.Academic)]
    [InlineData("restaurant autour de Bordeaux", SearchResultType.Local)]
    [InlineData("médecin proche de Lyon", SearchResultType.Local)]
    [InlineData("site officiel de Samsung", SearchResultType.OfficialSite)]
    [InlineData("quel est le meilleur téléphone", SearchResultType.General)]
    public void Detects_intent(string query, SearchResultType expected)
    {
        Assert.Equal(expected, QueryInterpreter.DetectType(query));
    }

    [Fact]
    public void Interpret_sets_type_and_language()
    {
        var request = _interpreter.Interpret("meilleur restaurant à Bordeaux", 8);

        Assert.Equal(SearchResultType.Local, request.Type);
        Assert.Equal("fr", request.Language);
        Assert.Equal(8, request.MaxResults);
    }

    [Fact]
    public void English_query_not_detected_as_french()
    {
        var request = _interpreter.Interpret("best smartphone for developers");

        Assert.Null(request.Language);
    }

    [Fact]
    public void Suggest_providers_for_video()
    {
        var request = new SearchRequest { Query = "video", Type = SearchResultType.Video };
        Assert.Equal(new[] { "youtube" }, QueryInterpreter.SuggestProviders(request));
    }

    [Fact]
    public void Suggest_providers_for_repository()
    {
        var request = new SearchRequest { Query = "repo", Type = SearchResultType.Repository };
        Assert.Equal(new[] { "github" }, QueryInterpreter.SuggestProviders(request));
    }

    [Fact]
    public void Suggest_providers_for_local()
    {
        var request = new SearchRequest { Query = "garage", Type = SearchResultType.Local };
        Assert.Equal(new[] { "nominatim" }, QueryInterpreter.SuggestProviders(request));
    }

    [Fact]
    public void Suggest_providers_for_general_returns_base_providers()
    {
        var request = new SearchRequest { Query = "general", Type = SearchResultType.General };
        var providers = QueryInterpreter.SuggestProviders(request);
        // Le socle "general" doit contenir DuckDuckGo + Bing + Wikipedia + News
        Assert.Contains("duckduckgo", providers);
        Assert.Contains("bing", providers);
        Assert.Contains("wikipedia", providers);
        Assert.Contains("news", providers);
    }

    [Fact]
    public void Suggest_providers_for_general_with_avis_adds_reddit()
    {
        var request = new SearchRequest { Query = "votre avis sur ce framework", Type = SearchResultType.General };
        var providers = QueryInterpreter.SuggestProviders(request);
        Assert.Contains("reddit", providers);
    }

    [Fact]
    public void Suggest_providers_for_general_with_tutoriel_adds_youtube()
    {
        var request = new SearchRequest { Query = "tutoriel python débutant", Type = SearchResultType.General };
        var providers = QueryInterpreter.SuggestProviders(request);
        Assert.Contains("youtube", providers);
    }

    [Fact]
    public void Suggest_providers_for_general_with_code_adds_tech_providers()
    {
        var request = new SearchRequest { Query = "comment fixer cette erreur python", Type = SearchResultType.General };
        var providers = QueryInterpreter.SuggestProviders(request);
        Assert.Contains("github", providers);
        Assert.Contains("stackoverflow", providers);
    }

    [Fact]
    public void Suggest_providers_for_general_with_location_adds_nominatim()
    {
        var request = new SearchRequest { Query = "restaurant près de Lyon", Type = SearchResultType.General };
        var providers = QueryInterpreter.SuggestProviders(request);
        Assert.Contains("nominatim", providers);
    }

    [Fact]
    public void Handles_whitespace_and_case()
    {
        var request = _interpreter.Interpret("   LE  GITHUB  DE  OLLAMA   ");

        Assert.Equal(SearchResultType.Repository, request.Type);
    }
}
