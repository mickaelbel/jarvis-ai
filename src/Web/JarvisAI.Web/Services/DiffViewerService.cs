using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

public interface IDiffViewerService
{
    DiffResult ComputeDiff(string oldText, string newText);
    string GenerateHtmlDiff(DiffResult diff);
    Task<string> GetFileDiffAsync(string filePath, CancellationToken ct = default);
}

public sealed class DiffViewerService : IDiffViewerService
{
    private readonly ILogger<DiffViewerService> _logger;

    public DiffViewerService(ILogger<DiffViewerService> logger)
    {
        _logger = logger;
    }

    public DiffResult ComputeDiff(string oldText, string newText)
    {
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');
        var changes = new List<DiffLine>();

        var maxLen = Math.Max(oldLines.Length, newLines.Length);
        for (int i = 0; i < maxLen; i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i] : null;
            var newLine = i < newLines.Length ? newLines[i] : null;

            if (oldLine == newLine)
            {
                changes.Add(new DiffLine { Type = DiffLineType.Same, OldLine = oldLine, NewLine = newLine, LineNumber = i + 1 });
            }
            else if (oldLine is null)
            {
                changes.Add(new DiffLine { Type = DiffLineType.Added, NewLine = newLine, LineNumber = i + 1 });
            }
            else if (newLine is null)
            {
                changes.Add(new DiffLine { Type = DiffLineType.Removed, OldLine = oldLine, LineNumber = i + 1 });
            }
            else
            {
                changes.Add(new DiffLine { Type = DiffLineType.Modified, OldLine = oldLine, NewLine = newLine, LineNumber = i + 1 });
            }
        }

        return new DiffResult
        {
            Changes = changes,
            Added = changes.Count(c => c.Type == DiffLineType.Added),
            Removed = changes.Count(c => c.Type == DiffLineType.Removed),
            Modified = changes.Count(c => c.Type == DiffLineType.Modified),
            Unchanged = changes.Count(c => c.Type == DiffLineType.Same)
        };
    }

    public string GenerateHtmlDiff(DiffResult diff)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<div class='diff-container'>");
        sb.AppendLine($"<div class='diff-stats'>+{diff.Added} -{diff.Removed} ~{diff.Modified} ={diff.Unchanged}</div>");
        sb.AppendLine("<pre class='diff-content'>");

        foreach (var line in diff.Changes)
        {
            var cssClass = line.Type switch
            {
                DiffLineType.Added => "diff-added",
                DiffLineType.Removed => "diff-removed",
                DiffLineType.Modified => "diff-modified",
                _ => "diff-same"
            };

            var prefix = line.Type switch
            {
                DiffLineType.Added => "+ ",
                DiffLineType.Removed => "- ",
                DiffLineType.Modified => "~ ",
                _ => "  "
            };

            var text = line.Type == DiffLineType.Removed ? line.OldLine : line.NewLine;
            sb.AppendLine($"<span class='{cssClass}'>{prefix}{System.Net.WebUtility.HtmlEncode(text ?? "")}</span>");
        }

        sb.AppendLine("</pre></div>");
        return sb.ToString();
    }

    public async Task<string> GetFileDiffAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return "File not found";

        var currentContent = await File.ReadAllTextAsync(filePath, ct);
        var diff = ComputeDiff("", currentContent);
        return GenerateHtmlDiff(diff);
    }
}

public sealed class DiffResult
{
    public List<DiffLine> Changes { get; set; } = new();
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Modified { get; set; }
    public int Unchanged { get; set; }
}

public sealed class DiffLine
{
    public DiffLineType Type { get; set; }
    public string? OldLine { get; set; }
    public string? NewLine { get; set; }
    public int LineNumber { get; set; }
}

public enum DiffLineType { Same, Added, Removed, Modified }
