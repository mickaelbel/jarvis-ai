using JarvisAI.Application.Agents;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class WebPageToolTests
{
    private readonly WebPageTool _tool = new(NullLogger<WebPageTool>.Instance);
    private readonly AgentContext _context = new("test command");

    private static IReadOnlyDictionary<string, string> Params(params string[] kv)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < kv.Length; i += 2)
            dict[kv[i]] = kv[i + 1];
        return dict;
    }

    [Fact]
    public async Task ExecuteAsync_requires_url()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "fetch"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_unknown_action()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "hack", "url", "https://example.com"));

        Assert.False(result.Success);
    }

    [Fact]
    public void ExtractLinks_resolves_relative_and_filters_other_schemes()
    {
        var html = "<html><body>" +
                   "<a href=\"/page2\">Deuxième page</a> " +
                   "<a href=\"https://externe.com/x\">Externe</a> " +
                   "<a href=\"javascript:void(0)\">JS</a> " +
                   "<a href=\"#ancre\">Ancre</a> " +
                   "<a href=\"mailto:a@b.c\">Mail</a>" +
                   "</body></html>";

        var links = WebPageTool.ExtractLinks(html, "https://site.com/base/page");

        Assert.Equal(2, links.Count);
        Assert.Contains("Deuxième page → https://site.com/page2", links[0]);
        Assert.Contains("Externe → https://externe.com/x", links[1]);
    }

    [Fact]
    public void ExtractLinks_handles_relative_href_without_leading_slash()
    {
        var links = WebPageTool.ExtractLinks("<a href=\"autre\">Autre</a>", "https://site.com/dossier/page.html");

        Assert.Single(links);
        Assert.Contains("https://site.com/dossier/autre", links[0]);
    }

    [Fact]
    public void ExtractLinks_no_links_returns_empty()
    {
        Assert.Empty(WebPageTool.ExtractLinks("<p>rien</p>", "https://site.com/"));
    }

    [Fact]
    public void ExtractForms_lists_action_method_and_named_inputs()
    {
        var html = "<html><body>" +
                   "<form action=\"/recherche\" method=\"get\">" +
                   "<input type=\"text\" name=\"q\" placeholder=\"Recherche...\">" +
                   "<input type=\"hidden\" name=\"lang\" value=\"fr\">" +
                   "<button type=\"submit\">Chercher</button>" +
                   "</form>" +
                   "<form action=\"/login\" method=\"post\">" +
                   "<input name=\"user\">" +
                   "<input name=\"pass\" type=\"password\">" +
                   "</form>" +
                   "</body></html>";

        var blocks = WebPageTool.ExtractForms(html, "https://site.com/");

        Assert.Equal(2, blocks.Count);
        Assert.Contains("Formulaire 1", blocks[0]);
        Assert.Contains("action=/recherche", blocks[0]);
        Assert.Contains("(méthode GET)", blocks[0]);
        Assert.Contains("- q [text] (placeholder : Recherche...)", blocks[0]);
        Assert.Contains("- lang [hidden] (valeur : fr)", blocks[0]);
        Assert.Contains("Formulaire 2", blocks[1]);
        Assert.Contains("action=/login", blocks[1]);
        Assert.Contains("- user [text]", blocks[1]);
        Assert.Contains("- pass [password]", blocks[1]);
    }

    [Fact]
    public void ExtractForms_caps_at_ten_with_note()
    {
        var html = new System.Text.StringBuilder();
        for (var i = 0; i < 12; i++)
            html.Append("<form><input name=\"f\"></form>");

        var blocks = WebPageTool.ExtractForms(html.ToString(), "https://site.com/");

        Assert.Equal(11, blocks.Count);
        Assert.Contains("... et d'autres formulaires.", blocks[^1]);
    }

    [Fact]
    public void ExtractForms_empty_page_returns_empty()
    {
        Assert.Empty(WebPageTool.ExtractForms("<p>rien</p>", "https://site.com/"));
    }

    [Fact]
    public void ExtractTitle_extracts_and_decodes_title()
    {
        Assert.Equal("Mon & Titre", WebPageTool.ExtractTitle("<html><head><title>Mon &amp; Titre</title></head></html>"));
        Assert.Null(WebPageTool.ExtractTitle("<html><body>pas de titre</body></html>"));
    }

    [Fact]
    public void StripHtml_removes_scripts_styles_and_tags()
    {
        var html = "<html><head><style>.x{color:red}</style><script>alert(1)</script></head><body><p>Bonjour&nbsp;le&nbsp;monde</p></body></html>";

        var text = WebPageTool.StripHtml(html);

        Assert.Equal("Bonjour le monde", text);
    }

    [Fact]
    public void ParseAttrs_reads_quoted_and_unquoted_attributes()
    {
        var attrs = WebPageTool.ParseAttrs("action=\"/go\" method=post id='formId'");

        Assert.Equal("/go", attrs["action"]);
        Assert.Equal("post", attrs["method"]);
        Assert.Equal("formId", attrs["id"]);
    }
}
