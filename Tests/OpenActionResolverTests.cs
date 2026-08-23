using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class OpenActionResolverTests
{
    private readonly OpenActionResolver _resolver = new();

    [Theory]
    [InlineData("ouvre la chaîne de MrBeast", "https://www.youtube.com/@MrBeast")]
    [InlineData("ouvre le github d'Ollama", "https://github.com/ollama/ollama")]
    [InlineData("ouvre le site Samsung", "https://samsung.com")]
    [InlineData("ouvre le site officiel de Discord", "https://discord.com")]
    [InlineData("ouvre le GitHub de Visual Studio Code", "https://github.com/microsoft/vscode")]
    [InlineData("ouvre le site de Firefox", "https://mozilla.org")]
    [InlineData("lance le site officiel d'OpenAI", "https://openai.com")]
    public void Resolves_direct_urls(string utterance, string expectedUrl)
    {
        var action = _resolver.Resolve(utterance);

        Assert.NotNull(action.DirectUrl);
        Assert.Equal(expectedUrl, action.DirectUrl);
        Assert.True(action.Confidence >= 0.5);
    }

    [Fact]
    public void Direct_http_url_is_returned_as_is()
    {
        var action = _resolver.Resolve("https://example.com/path?q=1");

        Assert.Equal("https://example.com/path?q=1", action.DirectUrl);
        Assert.Equal(1.0, action.Confidence);
    }

    [Fact]
    public void Unknown_entity_falls_back_to_search()
    {
        var action = _resolver.Resolve("ouvre le meilleur outil de dessin");

        Assert.Null(action.DirectUrl);
        Assert.NotNull(action.SearchQuery);
        Assert.Equal("search-fallback", action.Kind);
    }

    [Fact]
    public void Strip_verbs_removes_open_commands()
    {
        var stripped = OpenActionResolver.StripVerbs("ouvre le site officiel de samsung");

        Assert.DoesNotContain("ouvre", stripped);
    }

    [Fact]
    public void Guesses_entity_domain_when_unknown()
    {
        var action = _resolver.Resolve("va sur monetorise");

        Assert.NotNull(action.DirectUrl);
        Assert.Contains("monetorise", action.DirectUrl);
        Assert.Equal(0.5, action.Confidence);
    }

    [Fact]
    public void Empty_utterance_has_zero_confidence()
    {
        var action = _resolver.Resolve("   ");

        Assert.Equal(0, action.Confidence);
        Assert.Null(action.DirectUrl);
    }

    [Fact]
    public void School_url_is_preferred()
    {
        var action = _resolver.Resolve("ouvre Parcoursup");

        Assert.Equal("https://www.parcoursup.fr", action.DirectUrl);
    }
}
