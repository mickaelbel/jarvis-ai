using JarvisAI.Application.Agents;
using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// P19.3 — Une seule action utilisateur = une seule ouverture navigateur :
/// pas de boucle d'onglets, pas de doublons, limites de session.
/// </summary>
public sealed class BrowserOpenLoopPreventionTests
{
    private static (WebSearchTool tool, List<string> opened) CreateTool(params SearchResult[] results)
    {
        var verifier = new FakeLinkVerifier();
        var service = new WebSearchService(
            new IWebSearchProvider[] { new StubSearchProvider("duckduckgo", results) },
            verifier,
            new SearchCache(),
            new OfficialSiteDetector(),
            new FakeSiteDetector(),
            NullLogger<WebSearchService>.Instance);
        var opened = new List<string>();
        var tool = new WebSearchTool(service, NullLogger<WebSearchTool>.Instance, opened.Add);
        return (tool, opened);
    }

    private static SearchResult Result(string url)
        => new()
        {
            Title = "Résultat de test web",
            Url = url,
            Snippet = "snippet",
            Provider = "stub",
            Source = "wikipedia.org",
            Type = SearchResultType.General,
            Confidence = 0.8
        };

    [Fact]
    public async Task Open_action_opens_exactly_one_tab_for_one_request()
    {
        var (tool, opened) = CreateTool(
            Result("https://wikipedia.org/wiki/1"),
            Result("https://wikipedia.org/wiki/2"),
            Result("https://wikipedia.org/wiki/3"));

        var result = await tool.ExecuteAsync(
            new AgentContext("x"),
            new Dictionary<string, string> { ["action"] = "open", ["query"] = "meilleur site de test" });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(opened);
    }

    [Fact]
    public async Task Each_request_opens_at_most_one_tab()
    {
        var (tool, opened) = CreateTool(Result("https://wikipedia.org/wiki/Unique"));

        for (var i = 0; i < 5; i++)
        {
            var before = opened.Count;
            var result = await tool.ExecuteAsync(
                new AgentContext("x"),
                new Dictionary<string, string> { ["action"] = "open", ["query"] = "quel est le meilleur résultat de test web" });
            Assert.True(result.Success, result.ErrorMessage);
            // Exactement UNE ouverture par requête, jamais plusieurs.
            Assert.Equal(before + 1, opened.Count);
        }
    }

    [Fact]
    public void Browser_manager_rejects_duplicate_url_even_confirmed()
    {
        // No-op opener : AUCUN navigateur réel ne doit s'ouvrir pendant les tests.
        var mgr = new BrowserManager(NullLogger<BrowserManager>.Instance, _ => { });

        var first = mgr.OpenUrl("https://x.com/dup", confirmed: true);
        Assert.True(first.Success);

        mgr.ResetSessionInternal(); // contourne le cooldown, PAS l'historique

        var second = mgr.OpenUrl("https://x.com/dup", confirmed: true);
        Assert.False(second.Success);
        Assert.Contains("déjà", second.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loop_of_identical_urls_opens_single_tab()
    {
        var mgr = new BrowserManager(NullLogger<BrowserManager>.Instance, _ => { });

        var opened = 0;
        for (var i = 0; i < 20; i++)
        {
            var r = mgr.OpenUrl("https://x.com/loop", confirmed: true);
            if (r.Success) opened++;
        }

        // La boucle d'onglets est impossible : la 1ʳᵉ ouverture est la seule.
        Assert.Equal(1, opened);
    }

    [Fact]
    public void Auto_open_without_confirmation_is_capped_at_three()
    {
        var mgr = new BrowserManager(NullLogger<BrowserManager>.Instance, _ => { });

        // Trois auto-opens tolérés par session (avec reset du cooldown entre eux)
        Assert.True(mgr.OpenUrl("https://x.com/a").Success);
        mgr.ResetSessionInternal();
        Assert.True(mgr.OpenUrl("https://x.com/b").Success);
        mgr.ResetSessionInternal();
        var third = mgr.OpenUrl("https://x.com/c");
        // La 3ᵉ est la dernière sans confirmation : selon le cooldown elle peut
        // passer ou demander confirmation — on vérifie juste qu'à partir de 4
        // c'est bloqué.
        mgr.ResetSessionInternal();
        Assert.True(third.Success);

        var fourth = mgr.OpenUrl("https://x.com/d"); // doit demander confirmation
        Assert.False(fourth.Success);
        Assert.Contains("Limite", fourth.ErrorMessage);

        var confirmed = mgr.OpenUrl("https://x.com/e", confirmed: true);
        Assert.True(confirmed.Success);
    }

    [Fact]
    public void Browser_manager_uses_injected_opener_instead_of_real_browser()
    {
        var opened = new List<string>();
        var mgr = new BrowserManager(NullLogger<BrowserManager>.Instance, opened.Add);

        var result = mgr.OpenUrl("https://x.com/test", confirmed: true);

        Assert.True(result.Success);
        Assert.Equal(new[] { "https://x.com/test" }, opened);
        Assert.Single(mgr.History);
    }
}
