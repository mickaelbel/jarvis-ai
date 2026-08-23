using System.Text.RegularExpressions;
using Markdig;

namespace JarvisAI.Web.Services;

public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;
    private static readonly Regex CodeBlockRegex = new(
        @"<pre><code(?<attrs>[^>]*)>(?<content>.*?)</code></pre>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public MarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();
    }

    public string Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;
        var html = Markdown.ToHtml(markdown, _pipeline);
        return WrapCodeBlocks(html);
    }

    private static string WrapCodeBlocks(string html)
    {
        if (!html.Contains("<pre><code"))
            return html;

        return CodeBlockRegex.Replace(html, match =>
        {
            var attrs = match.Groups["attrs"].Value;
            var content = match.Groups["content"].Value;
            var lang = "text";
            var langMatch = Regex.Match(attrs, @"language-(?<lang>[a-zA-Z0-9_+\-]+)");
            if (langMatch.Success)
                lang = langMatch.Groups["lang"].Value;

            return "<div class=\"code-block\">" +
                   "<div class=\"code-header\">" +
                   "<span class=\"lang\">" + lang + "</span>" +
                   "<button onclick=\"jarvis.copyCode(this)\" title=\"Copy code\">" +
                   "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" width=\"12\" height=\"12\"><rect x=\"9\" y=\"9\" width=\"13\" height=\"13\" rx=\"2\" ry=\"2\"/><path d=\"M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1\"/></svg>" +
                   " Copy</button>" +
                   "</div>" +
                   "<pre><code" + attrs + ">" + content + "</code></pre>" +
                   "</div>";
        });
    }
}
