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

    [Fact]
    public void Does_not_strip_heading_tag_after_blockquote()
    {
        var html = _renderer.Render("> blockquote\n\n## My Heading\n\nContent");

        Assert.Contains("<h2", html);
        Assert.Contains(">My Heading</h2>", html);
        Assert.DoesNotContain("blockquote> id=\"my-heading\"", html);
        Assert.Contains("<p>Content</p>", html);
    }

    [Fact]
    public void Does_not_strip_heading_tag_after_paragraph()
    {
        var html = _renderer.Render("Some text.\n\n## My Heading\n\nMore text.");

        Assert.Contains("<h2", html);
        Assert.Contains(">My Heading</h2>", html);
        Assert.DoesNotContain("</p> id=\"my-heading\"", html);
    }

    [Fact]
    public void Does_not_strip_heading_tag_after_table()
    {
        var html = _renderer.Render("| A | B |\n|---|---|\n| 1 | 2 |\n\n## Section\n\nContent");

        Assert.Contains("<table>", html);
        Assert.Contains("<h2", html);
        Assert.Contains(">Section</h2>", html);
        Assert.DoesNotContain("</table> id=\"section\"", html);
    }
}
