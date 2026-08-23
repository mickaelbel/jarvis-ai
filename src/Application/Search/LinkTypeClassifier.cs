using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public static class LinkTypeClassifier
{
    public static string Detect(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "article";

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();
        var query = uri.Query.ToLowerInvariant();

        if (host == "youtube.com" || host == "www.youtube.com" || host == "m.youtube.com")
            return path.Contains("/shorts/") ? "video" : "video";
        if (host.EndsWith(".vimeo.com", StringComparison.Ordinal) || host == "vimeo.com")
            return "video";
        if (host.EndsWith(".youtu.be", StringComparison.Ordinal) || host == "youtu.be")
            return "video";
        if (host.EndsWith(".dailymotion.com", StringComparison.Ordinal))
            return "video";

        if (path.EndsWith(".pdf", StringComparison.Ordinal) || query.Contains("filetype=pdf", StringComparison.Ordinal))
            return "pdf";
        if (path.EndsWith(".epub", StringComparison.Ordinal))
            return "pdf";
        if (path.EndsWith(".doc", StringComparison.Ordinal) || path.EndsWith(".docx", StringComparison.Ordinal))
            return "document";
        if (path.EndsWith(".ppt", StringComparison.Ordinal) || path.EndsWith(".pptx", StringComparison.Ordinal))
            return "presentation";

        if (host.StartsWith("docs.", StringComparison.Ordinal) ||
            host.EndsWith(".readthedocs.io", StringComparison.Ordinal) ||
            host.EndsWith(".github.io", StringComparison.Ordinal) ||
            host == "learn.microsoft.com" ||
            Regex.IsMatch(path, @"/(docs|doc|learn|manual|documentation|reference|api|tutorials?|guides?)(/|$)"))
            return "docs";

        if (host.EndsWith(".gov", StringComparison.Ordinal) || host.EndsWith(".gouv.fr", StringComparison.Ordinal))
            return "article";

        return "article";
    }
}
