using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Search;

internal static class SearchHttp
{
    private static readonly string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    private static readonly Regex ScriptTag = new(@"<script[^>]*>.*?</script>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex StyleTag = new(@"<style[^>]*>.*?</style>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7");
        return client;
    }

    public static string DecodeRedirectUrl(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return href;

        var uddg = ExtractParam(href, "uddg=");
        if (uddg != null)
            return Uri.UnescapeDataString(uddg);

        var u = ExtractParam(href, "u=");
        if (u != null)
        {
            try
            {
                var padded = u.Replace('-', '+').Replace('_', '/');
                while (padded.Length % 4 != 0) padded += "=";
                var bytes = Convert.FromBase64String(padded);
                var decoded = System.Text.Encoding.UTF8.GetString(bytes);
                if (Uri.TryCreate(decoded, UriKind.Absolute, out _))
                    return decoded;
            }
            catch (FormatException)
            {
                // fall through to raw
            }
        }

        return href;
    }

    private static string? ExtractParam(string s, string marker)
    {
        var idx = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        var start = idx + marker.Length;
        var end = s.IndexOf('&', start);
        return end < 0 ? s[start..] : s[start..end];
    }

    public static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var text = ScriptTag.Replace(html, "");
        text = StyleTag.Replace(text, "");
        text = HtmlTag.Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = WhitespaceRun.Replace(text, " ").Trim();
        return text;
    }

    public static JsonDocument? TryParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
