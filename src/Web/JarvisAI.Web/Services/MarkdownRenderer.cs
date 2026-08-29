using System.Text.RegularExpressions;
using Markdig;

namespace JarvisAI.Web.Services;

public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;
    private static readonly Regex CodeBlockRegex = new(
        @"<pre><code(?<attrs>[^>]*)>(?<content>.*?)</code></pre>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ExcessiveBlankLines = new(
        @"(<br\s*/?>[\s]*){3,}",
        RegexOptions.Compiled);
    private static readonly Regex ExcessiveParagraphs = new(
        @"(</p>\s*<p[^>]*>){2,}",
        RegexOptions.Compiled);

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
        return CleanExcessiveLineBreaks(WrapCodeBlocks(html));
    }

    /// <summary>
    /// Rendu incrémental pour le streaming : gère les syntaxes incomplètes
    /// (code blocks non fermés, liens partiels, bold/italic) pour éviter
    /// le flash brut pendant le streaming.
    /// </summary>
    public string RenderIncremental(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        // Détecter les code blocks non fermés
        var fenceCount = 0;
        var inCodeBlock = false;
        foreach (Match m in Regex.Matches(markdown, @"^```", RegexOptions.Multiline))
            fenceCount++;
        inCodeBlock = fenceCount % 2 != 0;

        if (inCodeBlock)
        {
            // Fermer temporairement le code block pour le rendu
            return Render(markdown + "\n```\n");
        }

        // Détecter les liens partiels [text](sans fermeture)
        var linkOpen = Regex.Matches(markdown, @"\[[^\]]*\]\([^)]*$").Count;
        if (linkOpen > 0)
        {
            // Fermer temporairement le lien
            var fixedMd = markdown + ")";
            return Render(fixedMd);
        }

        return Render(markdown);
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

    /// <summary>
    /// Supprime les sauts de ligne excessifs (3+ &lt;br&gt; consécutifs ou 2+ &lt;p&gt; vides)
    /// pour un rendu plus compact. Les tableaux et code blocks ne sont pas affectés.
    /// </summary>
    private static string CleanExcessiveLineBreaks(string html)
    {
        // Réduire 3+ <br> consécutifs en un seul
        html = ExcessiveBlankLines.Replace(html, "<br/>");
        // Réduire 2+ <p> consécutifs en un seul
        html = ExcessiveParagraphs.Replace(html, "</p><p>");
        return html;
    }
}
