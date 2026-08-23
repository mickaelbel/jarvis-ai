using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class QueryRoutingExpandedTests
{
    [Theory]
    [InlineData("site officiel de microsoft", SearchResultType.OfficialSite)]
    [InlineData("official website microsoft", SearchResultType.OfficialSite)]
    [InlineData("short video", SearchResultType.Short)]
    [InlineData("shorts tiktok", SearchResultType.Short)]
    [InlineData("en direct sur twitch", SearchResultType.Live)]
    [InlineData("stream twitch", SearchResultType.Live)]
    [InlineData("playlist musique", SearchResultType.Playlist)]
    [InlineData("chaîne youtube", SearchResultType.Channel)]
    [InlineData("vidéo python", SearchResultType.Video)]
    [InlineData("video tutoriel", SearchResultType.Video)]
    [InlineData("github repo", SearchResultType.Repository)]
    [InlineData("dépôt git", SearchResultType.Repository)]
    [InlineData("actualité du jour", SearchResultType.News)]
    [InlineData("breaking news", SearchResultType.News)]
    [InlineData("dernières nouvelles", SearchResultType.News)]
    [InlineData("arxiv paper", SearchResultType.Academic)]
    [InlineData("recherche académique", SearchResultType.Academic)]
    [InlineData("publication scientifique", SearchResultType.Academic)]
    [InlineData("restaurant", SearchResultType.Local)]
    [InlineData("garage à Paris", SearchResultType.Local)]
    [InlineData("médecin de garde", SearchResultType.Local)]
    [InlineData("cinéma", SearchResultType.Local)]
    [InlineData("école", SearchResultType.Local)]
    [InlineData("météo demain", SearchResultType.General)]
    [InlineData("recette cuisine", SearchResultType.General)]
    public void Detects_request_type(string query, SearchResultType expected)
    {
        Assert.Equal(expected, QueryInterpreter.DetectType(query));
    }

    [Theory]
    [InlineData("le climat", "fr")]
    [InlineData("une recette", "fr")]
    [InlineData("installer le python", "fr")]
    [InlineData("python programming", null)]
    [InlineData("météo", null)]
    public void Detects_language(string query, string? expected)
    {
        Assert.Equal(expected, QueryInterpreter.DetectLanguage(query));
    }

    [Fact]
    public void Video_type_suggests_youtube_only()
    {
        var providers = QueryInterpreter.SuggestProviders(new SearchRequest { Query = "vidéo python", Type = SearchResultType.Video });
        Assert.Equal(new[] { "youtube" }, providers);
    }

    [Fact]
    public void Repository_type_suggests_github()
    {
        var providers = QueryInterpreter.SuggestProviders(new SearchRequest { Query = "github repo", Type = SearchResultType.Repository });
        Assert.Equal(new[] { "github" }, providers);
    }

    [Fact]
    public void News_type_suggests_news()
    {
        var providers = QueryInterpreter.SuggestProviders(new SearchRequest { Query = "actualité", Type = SearchResultType.News });
        Assert.Equal(new[] { "news" }, providers);
    }

    [Fact]
    public void Local_type_suggests_nominatim()
    {
        var providers = QueryInterpreter.SuggestProviders(new SearchRequest { Query = "restaurant", Type = SearchResultType.Local });
        Assert.Equal(new[] { "nominatim" }, providers);
    }

    [Fact]
    public void Academic_type_suggests_scholarly_providers()
    {
        var providers = QueryInterpreter.SuggestProviders(new SearchRequest { Query = "arxiv paper", Type = SearchResultType.Academic });
        Assert.Contains("arxiv", providers);
        Assert.Contains("semantic_scholar", providers);
        Assert.Contains("crossref", providers);
        Assert.Contains("wikipedia", providers);
    }

    [Fact]
    public void General_query_uses_default_provider_socle()
    {
        var providers = QueryInterpreter.SuggestProviders(new SearchRequest { Query = "météo demain", Type = SearchResultType.General });
        Assert.Equal(new[] { "duckduckgo", "bing", "wikipedia", "news" }, providers);
    }

    [Theory]
    [InlineData("avis sur iphone", "reddit")]
    [InlineData("recommandation ordinateur", "reddit")]
    [InlineData("tutoriel blender", "youtube")]
    [InlineData("how to install python", "youtube")]
    [InlineData("erreur c# stackoverflow", "github")]
    [InlineData("erreur c# stackoverflow", "stackoverflow")]
    [InlineData("bug dans mon code", "stackoverflow")]
    [InlineData("restaurant près de Lyon", "nominatim")]
    [InlineData("hôtel à Paris", "nominatim")]
    public void General_query_adds_contextual_provider(string query, string expectedProvider)
    {
        var providers = QueryInterpreter.SuggestProviders(new SearchRequest { Query = query, Type = SearchResultType.General });
        Assert.Contains(expectedProvider, providers);
    }

    [Fact]
    public void General_query_still_contains_base_providers()
    {
        var providers = QueryInterpreter.SuggestProviders(new SearchRequest { Query = "avis sur iphone", Type = SearchResultType.General });
        Assert.Contains("duckduckgo", providers);
        Assert.Contains("bing", providers);
        Assert.Contains("wikipedia", providers);
        Assert.Contains("news", providers);
    }
}
