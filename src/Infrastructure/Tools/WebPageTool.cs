using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Accès direct à une page web EN CODE (HTML brut) sans navigateur externe ni
/// service tiers : tout se fait en process via HttpClient. L'assistant peut
/// récupérer le code d'une page, lister ses liens, lister ses formulaires et
/// les envoyer (GET/POST) pour interagir automatiquement avec le site.
/// </summary>
public sealed class WebPageTool : ITool
{
    private const int MaxCodeChars = 50_000;
    private const int MaxLinks = 50;

    private static readonly HttpClient Http = BuildHttpClient();
    private readonly ILogger<WebPageTool> _logger;

    private static HttpClient BuildHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            UseCookies = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7");
        return client;
    }

    public string Name => "webpage";
    public string Description =>
        "Accès direct à une page web EN CODE (HTML brut) sans navigateur externe ni service tiers, avec interaction automatique. " +
        "Actions : fetch (récupère le code HTML de la page), links (liste les liens de la page), " +
        "forms (liste les formulaires et leurs champs), submit (envoie un formulaire GET/POST et renvoie le code de la réponse). " +
        "Utilise cet outil pour consulter une page sous forme de code et interagir automatiquement avec un site.";
    public string Category => "web";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "fetch | links | forms | submit", typeof(string), required: true),
        new ToolParameter("url", "URL de la page (fetch, links, forms, submit)", typeof(string)),
        new ToolParameter("mode", "Pour fetch : code (HTML brut, défaut) ou text (texte visible)", typeof(string)),
        new ToolParameter("fields", "Pour submit : champs du formulaire au format nom=valeur&nom2=valeur2", typeof(string)),
        new ToolParameter("method", "Pour submit : GET ou POST (défaut : POST si des champs sont fournis, sinon GET)", typeof(string)),
    };

    public WebPageTool(ILogger<WebPageTool> logger) => _logger = logger;

    public async Task<ToolResult> ExecuteAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("url", out var url);
        parameters.TryGetValue("mode", out var mode);
        parameters.TryGetValue("fields", out var fields);
        parameters.TryGetValue("method", out var method);

        try
        {
            return (action?.ToLowerInvariant()) switch
            {
                "fetch" => await FetchAsync(url, mode, cancellationToken),
                "links" => await LinksAsync(url, cancellationToken),
                "forms" => await FormsAsync(url, cancellationToken),
                "submit" => await SubmitAsync(url, fields, method, cancellationToken),
                _ => ToolResult.Failed($"Action inconnue : '{action}'. Actions valides : fetch, links, forms, submit.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WebPageTool] Action {Action} échouée sur {Url}", action, url);
            return ToolResult.Failed($"Erreur réseau : {ex.Message}");
        }
    }

    // ── fetch ────────────────────────────────────────────────────────────────
    private async Task<ToolResult> FetchAsync(string? url, string? mode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Failed("Paramètre 'url' requis.");

        var (finalUrl, html) = await GetHtmlAsync(url, ct);
        var title = ExtractTitle(html);
        var sb = new StringBuilder();
        sb.AppendLine($"Page : {finalUrl}");
        if (!string.IsNullOrEmpty(title))
            sb.AppendLine($"Titre : {title}");
        sb.AppendLine($"Taille : {html.Length} caractères.");

        if (string.Equals(mode, "text", StringComparison.OrdinalIgnoreCase))
        {
            var text = StripHtml(html);
            sb.AppendLine("Texte visible :");
            sb.AppendLine(Truncate(text, MaxCodeChars));
            return ToolResult.Succeeded(sb.ToString());
        }

        sb.AppendLine("Code HTML :");
        sb.AppendLine(Truncate(html, MaxCodeChars));
        return ToolResult.Succeeded(sb.ToString());
    }

    // ── links ────────────────────────────────────────────────────────────────
    private async Task<ToolResult> LinksAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Failed("Paramètre 'url' requis.");

        var (baseUrl, html) = await GetHtmlAsync(url, ct);
        var links = ExtractLinks(html, baseUrl);

        if (links.Count == 0)
            return ToolResult.Succeeded($"Aucun lien trouvé sur {baseUrl}.");

        var sb = new StringBuilder();
        sb.AppendLine($"Liens trouvés sur {baseUrl} ({links.Count}) :");
        for (var i = 0; i < links.Count; i++)
            sb.AppendLine($"{i + 1}. {links[i]}");
        return ToolResult.Succeeded(sb.ToString());
    }

    internal static List<string> ExtractLinks(string html, string baseUrl)
    {
        var links = new List<string>();
        var baseUri = new Uri(baseUrl);

        foreach (Match m in LinkRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value).Trim();
            if (string.IsNullOrEmpty(href)
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("#", StringComparison.Ordinal))
                continue;

            try
            {
                if (Uri.TryCreate(baseUri, href, out var abs))
                    href = abs.ToString();
            }
            catch { }

            var text = WebUtility.HtmlDecode(Regex.Replace(m.Groups["text"].Value, "<[^>]+>", "")).Trim();
            if (text.Length > 80) text = text[..80] + "...";
            links.Add(string.IsNullOrEmpty(text) ? href : $"{text} → {href}");

            if (links.Count >= MaxLinks) break;
        }

        return links;
    }

    // ── forms ────────────────────────────────────────────────────────────────
    private async Task<ToolResult> FormsAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Failed("Paramètre 'url' requis.");

        var (baseUrl, html) = await GetHtmlAsync(url, ct);
        var total = FormRegex.Matches(html).Count;
        if (total == 0)
            return ToolResult.Succeeded($"Aucun formulaire trouvé sur {baseUrl}.");

        var blocks = ExtractForms(html, baseUrl);
        var sb = new StringBuilder();
        sb.AppendLine($"Formulaires trouvés sur {baseUrl} ({total}) :");
        foreach (var block in blocks)
            sb.AppendLine(block);
        return ToolResult.Succeeded(sb.ToString());
    }

    internal static List<string> ExtractForms(string html, string baseUrl)
    {
        var blocks = new List<string>();
        var matches = FormRegex.Matches(html);
        var shown = Math.Min(matches.Count, 10);

        for (var index = 0; index < shown; index++)
        {
            var form = matches[index];
            var attrs = ParseAttrs(form.Groups[0].Value);
            var action = attrs.TryGetValue("action", out var a) && !string.IsNullOrWhiteSpace(a) ? a : baseUrl;
            var method = (attrs.TryGetValue("method", out var m) ? m : "get").ToUpperInvariant();
            var sb = new StringBuilder();
            sb.AppendLine($"Formulaire {index + 1} : action={action} (méthode {method})");

            foreach (Match input in InputRegex.Matches(form.Groups[1].Value))
            {
                var ia = ParseAttrs(input.Value);
                var name = ia.TryGetValue("name", out var n) ? n : "?";
                var type = ia.TryGetValue("type", out var t) ? t : "text";
                var placeholder = ia.TryGetValue("placeholder", out var p) ? p : "";
                var value = ia.TryGetValue("value", out var v) ? v : "";
                var extra = !string.IsNullOrEmpty(placeholder) ? $" (placeholder : {placeholder})" : !string.IsNullOrEmpty(value) ? $" (valeur : {value})" : "";
                sb.AppendLine($"  - {name} [{type}]{extra}");
            }

            blocks.Add(sb.ToString().TrimEnd());
        }

        if (matches.Count > shown)
            blocks.Add("... et d'autres formulaires.");
        return blocks;
    }

    // ── submit ───────────────────────────────────────────────────────────────
    private async Task<ToolResult> SubmitAsync(string? url, string? fields, string? method, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Failed("Paramètre 'url' requis.");

        var pageUrl = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : "https://" + url;

        var (finalUrl, html) = await GetHtmlAsync(pageUrl, ct);

        // Si l'URL pointe vers une page contenant un formulaire, on utilise
        // l'action et la méthode du premier formulaire (résolues absolument).
        var form = FormRegex.Match(html);
        if (form.Success)
        {
            var attrs = ParseAttrs(form.Groups[0].Value);
            if (attrs.TryGetValue("action", out var formAction) && !string.IsNullOrWhiteSpace(formAction))
            {
                try { pageUrl = new Uri(new Uri(finalUrl), formAction).ToString(); }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(method) && attrs.TryGetValue("method", out var formMethod))
                method = formMethod;
        }

        var effectiveMethod = (method ?? "").Trim().ToUpperInvariant();
        if (effectiveMethod is not ("GET" or "POST"))
            effectiveMethod = string.IsNullOrWhiteSpace(fields) ? "GET" : "POST";

        string resultHtml;
        if (effectiveMethod == "GET")
        {
            var target = pageUrl;
            if (!string.IsNullOrWhiteSpace(fields))
            {
                var sep = target.Contains('?') ? "&" : "?";
                target += sep + fields;
            }
            resultHtml = await Http.GetStringAsync(target, ct);
        }
        else
        {
            using var content = new StringContent(fields ?? string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await Http.PostAsync(pageUrl, content, ct);
            response.EnsureSuccessStatusCode();
            resultHtml = await response.Content.ReadAsStringAsync(ct);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Formulaire envoyé ({effectiveMethod}) : {pageUrl}");
        if (!string.IsNullOrWhiteSpace(fields))
            sb.AppendLine($"Champs : {fields}");
        sb.AppendLine($"Taille de la réponse : {resultHtml.Length} caractères.");
        sb.AppendLine("Code HTML de la réponse :");
        sb.AppendLine(Truncate(resultHtml, MaxCodeChars));
        return ToolResult.Succeeded(sb.ToString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static async Task<(string Url, string Html)> GetHtmlAsync(string url, CancellationToken ct)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Add("Cookie",
                "CONSENT=YES+cb.20240101-00-p0.en+FX+999; SOCS=CAISNQgDEitib3FfaWRlbnRpdHlmcm9udGVuZHVpc2VydmVyXzIwMjQwMTAxLjA3X3AxGgJlbiACGgYIgJnsBQ");
        }

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        return (finalUrl, html);
    }

    private static string Truncate(string text, int max)
        => text.Length > max ? text[..max] + $"\n\n[Contenu tronqué : {text.Length - max} caractères supplémentaires]" : text;

    internal static string? ExtractTitle(string html)
    {
        var m = TitleRegex.Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }

    internal static string StripHtml(string html)
    {
        var text = ScriptRegex.Replace(html, "");
        text = StyleRegex.Replace(text, "");
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    internal static Dictionary<string, string> ParseAttrs(string tag)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRegex.Matches(tag))
            attrs[m.Groups["key"].Value] = WebUtility.HtmlDecode(m.Groups["value"].Value);
        return attrs;
    }

    private static readonly Regex LinkRegex = new(
        @"<a\b[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FormRegex = new(
        @"<form\b[^>]*>(?<inner>.*?)</form\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex InputRegex = new(
        @"<(?:input|textarea|select)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AttrRegex = new(
        @"(?<key>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))",
        RegexOptions.Compiled);

    private static readonly Regex TitleRegex = new(
        @"<title[^>]*>(.*?)</title\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ScriptRegex = new(
        @"<script[^>]*>.*?</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex StyleRegex = new(
        @"<style[^>]*>.*?</style\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
}
