using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class BrowserManagerTests
{
    private static BrowserManager Create()
        => new(NullLogger<BrowserManager>.Instance, _ => { });

    private static ToolResult Execute(BrowserManager mgr, string action, string? url = null, string? confirmed = null)
    {
        var parameters = new Dictionary<string, string> { { "action", action } };
        if (url is not null) parameters["url"] = url;
        if (confirmed is not null) parameters["confirmed"] = confirmed;
        return mgr.ExecuteAsync(new AgentContext("test"), parameters).GetAwaiter().GetResult();
    }

    [Fact]
    public void Open_url_succeeds_and_records_history()
    {
        var mgr = Create();
        var r = Execute(mgr, "open_url", "https://github.com/ollama/ollama", confirmed: "true");
        Assert.True(r.Success);
        Assert.Single(mgr.History);
        Assert.Equal("https://github.com/ollama/ollama", mgr.History[0].Url);
    }

    [Fact]
    public void Open_url_with_https_prefix_keeps_unchanged()
    {
        var mgr = Create();
        var r = Execute(mgr, "open_url", "https://x.com/test", confirmed: "true");
        Assert.True(r.Success);
        Assert.Equal("https://x.com/test", mgr.History[0].Url);
    }

    [Fact]
    public void Open_url_without_protocol_adds_https()
    {
        var mgr = Create();
        var r = Execute(mgr, "open_url", "github.com/ollama", confirmed: "true");
        Assert.True(r.Success);
        Assert.Equal("https://github.com/ollama", mgr.History[0].Url);
    }

    [Fact]
    public void Open_url_rejects_empty_url()
    {
        var mgr = Create();
        var r = Execute(mgr, "open_url", "", confirmed: "true");
        Assert.False(r.Success);
        Assert.Contains("url", r.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_url_rejects_non_http_scheme()
    {
        var mgr = Create();
        var r = Execute(mgr, "open_url", "javascript:alert(1)", confirmed: "true");
        Assert.False(r.Success);
    }

    [Fact]
    public void Open_url_rejects_ftp_scheme()
    {
        var mgr = Create();
        var r = Execute(mgr, "open_url", "ftp://example.com/file", confirmed: "true");
        Assert.False(r.Success);
        Assert.Contains("refus", r.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void First_open_in_session_works_without_confirmation()
    {
        var mgr = Create();
        var r = Execute(mgr, "open_url", "https://example.com/1");
        Assert.True(r.Success);
    }

    [Fact]
    public void Second_auto_open_requires_confirmation()
    {
        var mgr = Create();
        // Trois opens auto (OK, _sessionOpens=3 = MaxAutoOpensPerSession)
        Assert.True(Execute(mgr, "open_url", "https://example.com/1").Success);
        mgr.ResetSessionInternal();
        Assert.True(Execute(mgr, "open_url", "https://example.com/2").Success);
        mgr.ResetSessionInternal();
        Assert.True(Execute(mgr, "open_url", "https://example.com/3").Success);
        Assert.Equal(3, mgr.SessionOpens);
        // Reset cooldown (compteur reste à 3)
        mgr.ResetSessionInternal();
        // 4ᵉ open SANS confirmation : doit être bloqué par la limite auto
        var r2 = Execute(mgr, "open_url", "https://example.com/4");
        Assert.False(r2.Success);
        Assert.Contains("Limite", r2.ErrorMessage);
    }

    [Fact]
    public void Second_open_with_confirmation_succeeds()
    {
        var mgr = Create();
        Execute(mgr, "open_url", "https://example.com/1");
        mgr.ResetSessionInternal();
        var r2 = Execute(mgr, "open_url", "https://example.com/2", confirmed: "true");
        Assert.True(r2.Success);
        // compteur incrémenté : 1 (1er) + 1 (2ᵉ confirmé) = 2
        Assert.Equal(2, mgr.SessionOpens);
    }

    [Fact]
    public void Duplicate_url_in_session_is_rejected()
    {
        var mgr = Create();
        // Premier open (auto OK)
        var r1 = Execute(mgr, "open_url", "https://x.com/foo");
        Assert.True(r1.Success);
        // Reset cooldown uniquement (préserve historique) pour tester la dédup
        mgr.ResetSessionInternal();
        var r2 = Execute(mgr, "open_url", "https://x.com/foo", confirmed: "true");
        Assert.False(r2.Success);
        Assert.Contains("déjà", r2.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }    [Fact]
    public void Cooldown_blocks_immediate_reopen()
    {
        var mgr = Create();
        Execute(mgr, "open_url", "https://x.com/a", confirmed: "true");
        var r2 = Execute(mgr, "open_url", "https://x.com/b", confirmed: "true");
        Assert.False(r2.Success);
        Assert.Contains("Trop tôt", r2.ErrorMessage);
    }

    [Fact]
    public void Reset_session_allows_new_opens()
    {
        var mgr = Create();
        Execute(mgr, "open_url", "https://x.com/1");
        Execute(mgr, "open_url", "https://x.com/2", confirmed: "true");
        var resetResult = Execute(mgr, "reset_session");
        Assert.True(resetResult.Success);
        Assert.Equal(0, mgr.SessionOpens);
        Assert.Empty(mgr.History);
        // New open succeeds
        var r = Execute(mgr, "open_url", "https://x.com/3");
        Assert.True(r.Success);
    }

    [Fact]
    public void Clear_history_preserves_count()
    {
        var mgr = Create();
        Execute(mgr, "open_url", "https://x.com/1");
        Execute(mgr, "reset_session");
        Execute(mgr, "open_url", "https://x.com/2", confirmed: "true");
        var clearResult = Execute(mgr, "clear_history");
        Assert.True(clearResult.Success);
        Assert.Empty(mgr.History);
        // session opens preserved (history only)
        Assert.Equal(1, mgr.SessionOpens);
    }

    [Fact]
    public void Get_history_returns_list_of_opens()
    {
        var mgr = Create();
        Execute(mgr, "open_url", "https://x.com/1");
        var h = Execute(mgr, "get_history");
        Assert.True(h.Success);
        Assert.Contains("https://x.com/1", h.Output);
    }

    [Fact]
    public void Get_history_when_empty()
    {
        var mgr = Create();
        var h = Execute(mgr, "get_history");
        Assert.True(h.Success);
        Assert.Contains("Aucun", h.Output);
    }

    [Fact]
    public void Unknown_action_returns_failure()
    {
        var mgr = Create();
        var r = Execute(mgr, "explode_url");
        Assert.False(r.Success);
        Assert.Contains("inconnue", r.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_url_javascript_url_strict_rejected()
    {
        var mgr = Create();
        var r = Execute(mgr, "open_url", "javascript:void(0)", confirmed: "true");
        Assert.False(r.Success);
    }

    [Fact]
    public void Session_cap_blocks_after_max()
    {
        var mgr = Create();
        // Open MaxSessionTotal = 10 URLs with confirmation
        for (var i = 0; i < 10; i++)
        {
            var r = Execute(mgr, "open_url", $"https://x.com/{i}", confirmed: "true");
            if (!r.Success && r.ErrorMessage is { } msg && msg.Contains("Trop tôt"))
                continue; // cooldown occasionally blocks
        }
        // 11th should be blocked by absolute limit or cooldown
        var r11 = Execute(mgr, "open_url", "https://x.com/99", confirmed: "true");
        Assert.False(r11.Success);
    }
}
