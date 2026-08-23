using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class RankingBonusTests
{
    private readonly ResultRanker _ranker = new();

    [Fact]
    public void Official_repo_gets_strong_bonus()
    {
        var regular = TestResults.Result("Repo A", "https://github.com/foo/bar", "github");
        regular.Confidence = 0.7;
        var official = TestResults.Result("Repo B", "https://github.com/ollama/ollama", "github");
        official.Confidence = 0.7;
        official.Type = SearchResultType.Repository;
        official.IsOfficial = true;

        var ranked = _ranker.Rank(new[] { regular, official });

        Assert.Equal("Repo B", ranked[0].Title);
    }

    [Fact]
    public void Official_channel_gets_strong_bonus()
    {
        var regular = TestResults.Result("Channel A", "https://www.youtube.com/@fake", "youtube");
        regular.Confidence = 0.7;
        var official = TestResults.Result("Channel B", "https://www.youtube.com/@MrBeast", "youtube");
        official.Confidence = 0.7;
        official.Type = SearchResultType.Channel;
        official.IsOfficial = true;

        var ranked = _ranker.Rank(new[] { regular, official });

        Assert.Equal("Channel B", ranked[0].Title);
    }

    [Fact]
    public void School_domain_gets_bonus()
    {
        var regular = TestResults.Result("Random", "https://x.com/foo", "duckduckgo");
        regular.Confidence = 0.7;
        var school = TestResults.Result("École", "https://www.ac-bordeaux.fr", "duckduckgo");
        school.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { regular, school });

        Assert.Equal("École", ranked[0].Title);
    }

    [Fact]
    public void School_domain_starts_with_ac_dash()
    {
        var regular = TestResults.Result("Random", "https://x.com/foo", "duckduckgo");
        regular.Confidence = 0.7;
        var academy = TestResults.Result("Académie", "https://www.ac-paris.fr", "duckduckgo");
        academy.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { regular, academy });

        Assert.Equal("Académie", ranked[0].Title);
    }

    [Fact]
    public void Constructor_domain_gets_bonus_when_official()
    {
        var regular = TestResults.Result("Random", "https://x.com/foo", "duckduckgo");
        regular.Confidence = 0.7;
        var nonOfficial = TestResults.Result("Apple A", "https://apple.com/macbook", "duckduckgo");
        nonOfficial.Confidence = 0.7;
        nonOfficial.IsOfficial = false;
        var official = TestResults.Result("Apple B", "https://apple.com/macbook-pro", "duckduckgo");
        official.Confidence = 0.7;
        official.IsOfficial = true;

        var ranked = _ranker.Rank(new[] { regular, nonOfficial, official });

        Assert.Equal("Apple B", ranked[0].Title);
    }

    [Fact]
    public void Higher_documentation_bonus_than_before()
    {
        // Docs bonuses have been increased from 6 to 10
        var regular = TestResults.Result("Regular", "https://x.com/1", "duckduckgo");
        regular.Confidence = 0.7;
        var docs = TestResults.Result("Docs", "https://docs.python.org/3/tutorial/", "duckduckgo");
        docs.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { regular, docs });

        Assert.Equal("Docs", ranked[0].Title);
    }

    [Fact]
    public void Media_bonus_increased()
    {
        var regular = TestResults.Result("Regular", "https://x.com/1", "duckduckgo");
        regular.Confidence = 0.7;
        var media = TestResults.Result("Media", "https://www.lemonde.fr/actu", "duckduckgo");
        media.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { regular, media });

        Assert.Equal("Media", ranked[0].Title);
    }
}

public sealed class OfficialCatalogEnrichmentTests
{
    [Fact]
    public void Recognizes_chrome_as_official_channel()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialChannel("google", out var url));
        Assert.Contains("google", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognizes_microsoft_as_official_channel()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialChannel("microsoft", out var url));
        Assert.Contains("microsoft", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognizes_apple_as_official_channel()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialChannel("apple", out var url));
        Assert.Contains("apple", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognizes_nvidia_as_official_channel()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialChannel("nvidia", out var url));
        Assert.Contains("nvidia", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognizes_openai_as_official_channel()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialChannel("openai", out var url));
        Assert.Contains("openai", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognizes_anthropic_as_official_channel()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialChannel("anthropic", out var url));
        Assert.Contains("anthropic", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognizes_llama_cpp_repo()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialRepo("llama.cpp", out var repo));
        Assert.Equal("ggerganov/llama.cpp", repo);
    }

    [Fact]
    public void Recognizes_whisper_cpp_repo()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialRepo("whisper.cpp", out var repo));
        Assert.Equal("ggerganov/whisper.cpp", repo);
    }

    [Fact]
    public void Recognizes_piper_repo()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialRepo("piper", out var repo));
        Assert.Equal("rhasspy/piper", repo);
    }

    [Fact]
    public void Recognizes_comfyui_repo()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialRepo("comfyui", out var repo));
        Assert.Equal("comfyanonymous/ComfyUI", repo);
    }

    [Fact]
    public void Recognizes_vllm_repo()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialRepo("vllm", out var repo));
        Assert.Equal("vllm-project/vllm", repo);
    }

    [Fact]
    public void Recognizes_polytechnique_school()
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl("polytechnique", out var url));
        Assert.Equal("https://www.polytechnique.edu", url);
    }

    [Fact]
    public void Recognizes_hec_school()
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl("hec", out var url));
        Assert.Equal("https://www.hec.edu", url);
    }

    [Fact]
    public void Recognizes_insa_school()
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl("insa", out var url));
        Assert.Contains("insa", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognizes_cpge_school()
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl("cpge", out _));
    }

    [Fact]
    public void Recognizes_sti2d_school()
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl("sti2d", out _));
    }

    [Fact]
    public void Recognizes_prepa_integree_school()
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl("prépa intégrée", out _));
    }

    [Fact]
    public void Recognizes_ecole_ingenieur_school()
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl("école d'ingénieurs", out _));
    }
}
