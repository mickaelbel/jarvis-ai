using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class OfficialSiteDetectionTests
{
    private readonly OfficialSiteDetector _detector = new();

    [Theory]
    [InlineData("ouvre le site officiel de Discord", "discord.com")]
    [InlineData("site officiel Discord", "discord.com")]
    [InlineData("telecharger Firefox officiel", "mozilla.org")]
    [InlineData("Samsung site officiel", "samsung.com")]
    [InlineData("Microsoft officiel", "microsoft.com")]
    [InlineData("GitHub officiel", "github.com")]
    [InlineData("Visual Studio officiel", "visualstudio.microsoft.com")]
    [InlineData("OpenAI site officiel", "openai.com")]
    [InlineData("Ollama officiel", "ollama.com")]
    [InlineData("site officiel de l'Etudiant", "letudiant.fr")]
    [InlineData("Parcoursup officiel", "parcoursup.fr")]
    [InlineData("Onisep site officiel", "onisep.fr")]
    [InlineData("site officiel Studyrama", "studyrama.com")]
    [InlineData("Le Monde journal officiel", "lemonde.fr")]
    [InlineData("FranceInfo actualite officiel", "franceinfo.fr")]
    [InlineData("BBC news official", "bbc.com")]
    [InlineData("The Verge official", "theverge.com")]
    [InlineData("Ars Technica official", "arstechnica.com")]
    [InlineData("TechCrunch official", "techcrunch.com")]
    public void Resolves_known_official_domains(string utterance, string expectedDomain)
    {
        var match = _detector.ResolveOfficial(utterance);

        Assert.NotNull(match);
        Assert.Contains(expectedDomain, match!.Url);
        Assert.True(match.Confidence >= 0.9);
    }

    [Fact]
    public void Resolves_official_repo_when_github_mentioned()
    {
        var match = _detector.ResolveOfficial("le github d'Ollama");

        Assert.NotNull(match);
        Assert.Equal("https://github.com/ollama/ollama", match!.Url);
        Assert.Equal("github-repo", match.MatchKind);
    }

    [Fact]
    public void Resolves_official_github_repo_for_vscode()
    {
        var match = _detector.ResolveOfficial("ouvre le github de Visual Studio Code");

        Assert.NotNull(match);
        Assert.Equal("https://github.com/microsoft/vscode", match!.Url);
    }

    [Fact]
    public void Resolves_youtube_channel()
    {
        var match = _detector.ResolveOfficial("ouvre la chaîne de MrBeast");

        Assert.NotNull(match);
        Assert.Equal("https://www.youtube.com/@MrBeast", match!.Url);
        Assert.Equal("youtube-channel", match.MatchKind);
    }

    [Fact]
    public void Resolves_direct_url()
    {
        var match = _detector.ResolveOfficial("https://example.com/page");

        Assert.NotNull(match);
        Assert.Equal("https://example.com/page", match!.Url);
        Assert.Equal("direct-url", match.MatchKind);
    }

    [Fact]
    public void Resolves_school_academy()
    {
        var match = _detector.ResolveOfficial("académie de Bordeaux site officiel");

        Assert.NotNull(match);
        Assert.Equal("https://www.ac-bordeaux.fr", match!.Url);
    }

    [Theory]
    [InlineData("https://www.discord.com", true)]
    [InlineData("https://discord.com/app", true)]
    [InlineData("https://github.com", true)]
    [InlineData("https://fr.wikipedia.org/wiki/Discord", true)]
    [InlineData("https://www.samsung.com", true)]
    [InlineData("https://microsoft.com", true)]
    [InlineData("https://stackoverflow.com/questions/1", true)]
    [InlineData("https://code.visualstudio.com", true)]
    [InlineData("https://www.letudiant.fr", true)]
    [InlineData("https://www.parcoursup.fr", true)]
    public void Detects_official_urls(string url, bool expected)
    {
        Assert.Equal(expected, _detector.IsOfficialUrl(url));
    }

    [Theory]
    [InlineData("https://discord.com.fake.net", false)]
    [InlineData("https://samsung-support.xyz", false)]
    [InlineData("https://disc0rd.com", false)]
    [InlineData("https://some-random-blog.com", false)]
    [InlineData("https://fr.wikipedia.org", true)]
    [InlineData("https://mozilla.org/firefox", true)]
    public void Rejects_lookalike_official_urls(string url, bool expected)
    {
        Assert.Equal(expected, _detector.IsOfficialUrl(url));
    }

    [Fact]
    public void Returns_null_for_unknown_entity()
    {
        Assert.Null(_detector.ResolveOfficial("quel est le meilleur téléphone ?"));
    }

    [Fact]
    public void Handles_whitespace_and_case()
    {
        var match = _detector.ResolveOfficial("   OUVRE  LE SITE OFFICIEL DE SAMSUNG  ");

        Assert.NotNull(match);
        Assert.Contains("samsung.com", match!.Url);
    }
}
