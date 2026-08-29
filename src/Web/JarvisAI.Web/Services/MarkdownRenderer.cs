using System.Text.RegularExpressions;
using Markdig;

namespace JarvisAI.Web.Services;

public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;
    private static readonly Regex CodeBlockRegex = new(
        @"<pre><code(?<attrs>[^>]*)>(?<content>.*?)</code></pre>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ConsecutiveParagraphs = new(
        @"</p>\s*\n?\s*<p>",
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
    /// Merge les &lt;p&gt; consécutifs en un seul paragraphe avec &lt;br/&gt; entre eux.
    /// Markdig crée un &lt;p&gt; par ligne de texte, et le margin CSS de chaque &lt;p&gt;
    /// crée un espacement visuel excessif. En mergeant, on élimine ce margin.
    /// Les tableaux et code blocks ne sont pas affectés (ils ne sont pas dans des &lt;p&gt;).
    /// </summary>
    private static string CleanExcessiveLineBreaks(string html)
    {
        // Merge </p>\n<p> en <br/> — fusionne les paragraphes consécutifs
        html = ConsecutiveParagraphs.Replace(html, "<br/>");
        return html;
    }
}
