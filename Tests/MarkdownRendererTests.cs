using JarvisAI.Web.Services;

namespace JarvisAI.Tests;

public class MarkdownRendererTests
{
    private readonly MarkdownRenderer _renderer = new();

    [Fact]
    public void Wraps_fenced_code_in_code_block_with_language_and_copy_button()
    {
        var html = _renderer.Render("```csharp\nvar x = 1;\n```");

        Assert.Contains("class=\"code-block\"", html);
        Assert.Contains("class=\"code-header\"", html);
        Assert.Contains("class=\"lang\"", html);
        Assert.Contains(">csharp<", html);
        Assert.Contains("jarvis.copyCode(this)", html);
        Assert.Contains("<pre><code class=\"language-csharp\">", html);
    }

    [Fact]
    public void Renders_pipe_tables()
    {
        var html = _renderer.Render("| A | B |\n|---|---|\n| 1 | 2 |");

        Assert.Contains("<table>", html);
        Assert.Contains("<th>", html);
    }

    [Fact]
    public void Leaves_inline_code_unwrapped()
    {
        var html = _renderer.Render("Use `dotnet build` now.");

        Assert.Contains("<code>dotnet build</code>", html);
        Assert.DoesNotContain("code-block", html);
    }

    [Fact]
    public void Escapes_raw_html()
    {
        var html = _renderer.Render("Hello <b>world</b>");

        Assert.DoesNotContain("<b>world</b>", html);
        Assert.Contains("&lt;b&gt;", html);
    }
}
