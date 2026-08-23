using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ReadDocumentTool : ITool
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".xml", ".csv", ".html", ".htm", ".ini", ".cfg", ".yml", ".yaml"
    };

    public string Name => "read_document";
    public string Description =>
        "Extracts the text content of a local document so you can answer about it. " +
        "Supported formats: PDF (.pdf), Word (.docx), and text files (.txt, .md, .json, .csv, .xml, .log...). " +
        "Returns the text truncated to max_chars characters.";
    public string Category => "filesystem";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("path", "Absolute path of the document to read", typeof(string), required: true),
        new ToolParameter("max_chars", "Maximum number of characters to return (default 20000)", typeof(int), defaultValue: 20000)
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!parameters.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
            {
                return Task.FromResult(ToolResult.Failed("Missing required parameter 'path'."));
            }

            path = UserPaths.ResolveUserPath(path) ?? path;

            if (!File.Exists(path))
            {
                return Task.FromResult(ToolResult.Failed($"File not found: {path}"));
            }

            var maxChars = 20000;
            if (parameters.TryGetValue("max_chars", out var maxCharsRaw)
                && int.TryParse(maxCharsRaw, out var parsed) && parsed > 0)
            {
                maxChars = parsed;
            }

            var extension = Path.GetExtension(path);
            var text = extension.ToLowerInvariant() switch
            {
                ".pdf" => ExtractPdf(path),
                ".docx" => ExtractDocx(path),
                _ when TextExtensions.Contains(extension) => File.ReadAllText(path),
                _ => $"Unsupported file type: {extension}. Supported: pdf, docx, txt, md, json, csv, xml, log."
            };

            if (text.StartsWith("Unsupported file type"))
            {
                return Task.FromResult(ToolResult.Failed(text));
            }

            var totalChars = text.Length;
            if (text.Length > maxChars)
            {
                text = text.Substring(0, maxChars) + "\n...[tronqué — total : " + totalChars + " caractères]";
            }

            return Task.FromResult(ToolResult.Succeeded(text));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed("Erreur de lecture du document : " + ex.Message));
        }
    }

    private static string ExtractPdf(string path)
    {
        var sb = new StringBuilder();
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
                sb.AppendLine();
            }
        }
        return sb.ToString().Trim();
    }

    private static string ExtractDocx(string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("Le document DOCX ne contient pas word/document.xml.");
        using var stream = entry.Open();
        var xml = XDocument.Load(stream);
        var paragraphs = xml.Descendants(XName.Get("p", "http://schemas.openxmlformats.org/wordprocessingml/2006/main"));
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            sb.AppendLine(p.Value.Trim());
        }
        return sb.ToString().Trim();
    }
}
