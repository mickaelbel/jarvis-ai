using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Web.Services;

public sealed class DocumentationTool : ToolBase
{
    private readonly IApiDocumentationGenerator _docs;
    private readonly IDocumentationUpdaterService _updater;

    private static readonly Regex ClassRegex = new(
        @"^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+)*class\s+(\w+)",
        RegexOptions.Multiline);

    private static readonly Regex MethodRegex = new(
        @"public\s+[\w<>\[\],\.\s?]+\s+(\w+)\s*\(",
        RegexOptions.Multiline);

    public override string Name => "documentation";
    public override string Description => "Génère la documentation d'une API (action: api) ou du code source (action: source) en markdown et HTML autonome.";
    public override string Category => "dev";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "api (documentation d'un type) ou source (documentation du code)", typeof(string), required: true),
        new ToolParameter("type_name", "Nom du type à documenter (requis si action=api)", typeof(string)),
        new ToolParameter("source_path", "Fichier ou dossier de code source à analyser (requis si action=source)", typeof(string)),
        new ToolParameter("output_dir", "Dossier de sortie (optionnel, défaut: docs)", typeof(string)),
    };

    public DocumentationTool(
        IApiDocumentationGenerator docs,
        IDocumentationUpdaterService updater,
        ILogger<DocumentationTool> logger) : base(logger)
    {
        _docs = docs;
        _updater = updater;
    }

    protected override Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        return action switch
        {
            "api" => Task.FromResult(GenerateApiDoc(parameters)),
            "source" => Task.FromResult(GenerateSourceDoc(parameters)),
            _ => Task.FromResult(Fail($"Action inconnue: {action}. Valides: api, source"))
        };
    }

    private ToolResult GenerateApiDoc(IReadOnlyDictionary<string, string> parameters)
    {
        var typeName = RequireParam(parameters, "type_name");
        var type = FindType(typeName);
        if (type is null)
            return Fail($"Type introuvable: {typeName}");

        var outputDir = GetOutputDir(parameters);
        var markdown = _docs.GenerateDocumentation(type);
        var html = _docs.GenerateDocumentationHtml(type);

        var safeName = SanitizeFileName(type.Name);
        var mdPath = Path.Combine(outputDir, $"API_{safeName}.md");
        var htmlPath = Path.Combine(outputDir, $"API_{safeName}.html");

        File.WriteAllText(mdPath, markdown, Encoding.UTF8);
        File.WriteAllText(htmlPath, html, Encoding.UTF8);

        var extract = markdown.Length > 500 ? markdown[..500] : markdown;
        return Ok($"Documentation générée pour '{type.FullName}' dans {outputDir}:\n- {mdPath}\n- {htmlPath}\n\nExtrait:\n{extract}");
    }

    private ToolResult GenerateSourceDoc(IReadOnlyDictionary<string, string> parameters)
    {
        var sourcePath = RequireParam(parameters, "source_path");
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            return Fail($"Chemin introuvable: {sourcePath}");

        var outputDir = GetOutputDir(parameters);
        var files = Directory.Exists(sourcePath)
            ? Directory.GetFiles(sourcePath, "*.cs", SearchOption.AllDirectories)
            : new[] { sourcePath };

        var fileDocs = new List<(string File, List<string> Classes, List<string> Methods)>();
        var changes = new List<DocumentationChange>();
        int totalClasses = 0;
        int totalMethods = 0;

        foreach (var file in files)
        {
            string content;
            try
            {
                if (!File.Exists(file)) continue;
                content = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            var classes = ClassRegex.Matches(content)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();
            var methods = MethodRegex.Matches(content)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            totalClasses += classes.Count;
            totalMethods += methods.Count;
            fileDocs.Add((file, classes, methods));

            foreach (var c in classes)
                changes.Add(new DocumentationChange { Type = ChangeType.Added, Name = c, Description = $"Classe {c}" });
            foreach (var m in methods)
                changes.Add(new DocumentationChange { Type = ChangeType.Added, Name = m, Description = $"Méthode publique {m}()" });
        }

        if (fileDocs.Count == 0)
            return Fail($"Aucun fichier .cs analysable sous: {sourcePath}");

        var md = new StringBuilder();
        md.AppendLine("# Documentation source");
        md.AppendLine();
        md.AppendLine($"Générée le {DateTime.Now:dd/MM/yyyy HH:mm} depuis **{sourcePath}**");
        md.AppendLine();
        md.AppendLine($"Fichiers analysés: {fileDocs.Count} | Classes: {totalClasses} | Méthodes publiques: {totalMethods}");
        md.AppendLine();
        md.AppendLine("## Fichiers");
        md.AppendLine();

        foreach (var (file, classes, methods) in fileDocs)
        {
            md.AppendLine($"### {file}");
            if (classes.Count > 0)
                md.AppendLine($"- Classes: {string.Join(", ", classes)}");
            if (methods.Count > 0)
            {
                md.AppendLine("- Méthodes publiques:");
                foreach (var m in methods)
                    md.AppendLine($"  - `{m}()`");
            }
            md.AppendLine();
        }

        md.AppendLine(_updater.GenerateChangelog(changes));

        var html = BuildSourceDocHtml(sourcePath, fileDocs);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var mdPath = Path.Combine(outputDir, $"SourceDoc_{stamp}.md");
        var htmlPath = Path.Combine(outputDir, $"SourceDoc_{stamp}.html");

        File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);
        File.WriteAllText(htmlPath, html, Encoding.UTF8);

        var uniqueClasses = fileDocs.SelectMany(f => f.Classes).Distinct().Count();
        var uniqueMethods = fileDocs.SelectMany(f => f.Methods).Distinct().Count();
        return Ok($"Documentation source générée dans {outputDir}:\n- {mdPath}\n- {htmlPath}\n\nFichiers: {fileDocs.Count}, Classes: {uniqueClasses}, Méthodes publiques: {uniqueMethods}");
    }

    private static string GetOutputDir(IReadOnlyDictionary<string, string> parameters)
    {
        var dir = parameters.TryGetValue("output_dir", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : Path.Combine(Directory.GetCurrentDirectory(), "docs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Type? FindType(string typeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    var matched = type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                                  || (type.FullName is not null && type.FullName.EndsWith("." + typeName, StringComparison.OrdinalIgnoreCase));
                    if (!matched) continue;
                    if (type.FullName == typeName) return type;
                    return type;
                }
            }
            catch
            {
                // certaines assemblies échouent à l'énumération des types
            }
        }
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string BuildSourceDocHtml(string sourcePath, List<(string File, List<string> Classes, List<string> Methods)> fileDocs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"fr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>Documentation source</title>");
        sb.AppendLine("<link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css\" rel=\"stylesheet\">");
        sb.AppendLine("</head>");
        sb.AppendLine("<body class=\"bg-light\">");
        sb.AppendLine("<div class=\"container py-4\">");
        sb.AppendLine("<h1 class=\"mb-4\">Documentation source</h1>");
        sb.AppendLine($"<p>Source: <code>{WebUtility.HtmlEncode(sourcePath)}</code></p>");
        sb.AppendLine("<table class=\"table table-striped table-hover bg-white\">");
        sb.AppendLine("<thead class=\"table-dark\"><tr><th>Fichier</th><th>Classes</th><th>Méthodes</th></tr></thead>");
        sb.AppendLine("<tbody>");

        foreach (var (file, classes, methods) in fileDocs)
        {
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td><code>{WebUtility.HtmlEncode(file)}</code></td>");
            sb.AppendLine($"<td>{string.Join("<br>", classes.Select(WebUtility.HtmlEncode))}</td>");
            sb.AppendLine($"<td>{string.Join("<br>", methods.Select(m => WebUtility.HtmlEncode(m + "()")))}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}