using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Web.Services;

public interface IArchitectureDiagramGenerator
{
    string GenerateFromProject(string projectPath);
    string GenerateFromCode(string code, string language);
    string GenerateMermaidDiagram(List<DiagramNode> nodes, List<DiagramEdge> edges);
    List<DiagramNode> AnalyzeDependencies(string projectPath);
}

public sealed class ArchitectureDiagramGenerator : IArchitectureDiagramGenerator
{
    private readonly ILogger<ArchitectureDiagramGenerator> _logger;

    public ArchitectureDiagramGenerator(ILogger<ArchitectureDiagramGenerator> logger)
    {
        _logger = logger;
    }

    public string GenerateFromProject(string projectPath)
    {
        var nodes = AnalyzeDependencies(projectPath);
        var edges = new List<DiagramEdge>();

        // Generate edges from project references
        foreach (var node in nodes)
        {
            foreach (var dep in node.Dependencies)
            {
                edges.Add(new DiagramEdge
                {
                    From = node.Name,
                    To = dep,
                    Label = "depends on"
                });
            }
        }

        return GenerateMermaidDiagram(nodes, edges);
    }

    public string GenerateFromCode(string code, string language)
    {
        var nodes = new List<DiagramNode>();
        var edges = new List<DiagramEdge>();

        // Extract classes
        var classMatches = System.Text.RegularExpressions.Regex.Matches(code, @"class\s+(\w+)");
        foreach (System.Text.RegularExpressions.Match match in classMatches)
        {
            nodes.Add(new DiagramNode
            {
                Id = match.Groups[1].Value,
                Name = match.Groups[1].Value,
                Type = NodeType.Class
            });
        }

        // Extract interfaces
        var interfaceMatches = System.Text.RegularExpressions.Regex.Matches(code, @"interface\s+(\w+)");
        foreach (System.Text.RegularExpressions.Match match in interfaceMatches)
        {
            nodes.Add(new DiagramNode
            {
                Id = match.Groups[1].Value,
                Name = match.Groups[1].Value,
                Type = NodeType.Interface
            });
        }

        // Extract dependencies (inheritance, implementation)
        var inheritsMatches = System.Text.RegularExpressions.Regex.Matches(code, @"class\s+(\w+)\s*:\s*(\w+)");
        foreach (System.Text.RegularExpressions.Match match in inheritsMatches)
        {
            edges.Add(new DiagramEdge
            {
                From = match.Groups[1].Value,
                To = match.Groups[2].Value,
                Label = "inherits"
            });
        }

        return GenerateMermaidDiagram(nodes, edges);
    }

    public string GenerateMermaidDiagram(List<DiagramNode> nodes, List<DiagramEdge> edges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("graph TD");

        // Add nodes
        foreach (var node in nodes)
        {
            var shape = node.Type switch
            {
                NodeType.Class => $"[{node.Name}]",
                NodeType.Interface => $"[{node.Name}]{{:",
                NodeType.Service => $"[{node.Name}]",
                NodeType.Database => $"[{node.Name}]{{(DB)}}",
                NodeType.API => $"[{node.Name}]",
                _ => $"[{node.Name}]"
            };
            sb.AppendLine($"    {node.Id}{shape}");
        }

        sb.AppendLine();

        // Add edges
        foreach (var edge in edges)
        {
            if (!string.IsNullOrEmpty(edge.Label))
                sb.AppendLine($"    {edge.From} -->|{edge.Label}| {edge.To}");
            else
                sb.AppendLine($"    {edge.From} --> {edge.To}");
        }

        return sb.ToString();
    }

    public List<DiagramNode> AnalyzeDependencies(string projectPath)
    {
        var nodes = new List<DiagramNode>();

        try
        {
            if (File.Exists(projectPath) && projectPath.EndsWith(".csproj"))
            {
                var content = File.ReadAllText(projectPath);
                var name = Path.GetFileNameWithoutExtension(projectPath);

                nodes.Add(new DiagramNode
                {
                    Id = name,
                    Name = name,
                    Type = NodeType.Class
                });

                // Extract package references
                var packageMatches = System.Text.RegularExpressions.Regex.Matches(content,
                    @"<PackageReference\s+Include=""([^""]+)""");

                foreach (System.Text.RegularExpressions.Match match in packageMatches)
                {
                    var pkg = match.Groups[1].Value;
                    if (!nodes.Any(n => n.Name == pkg))
                    {
                        nodes.Add(new DiagramNode
                        {
                            Id = pkg,
                            Name = pkg,
                            Type = NodeType.Library,
                            Dependencies = new List<string>()
                        });
                    }
                }

                // Extract project references
                var projectMatches = System.Text.RegularExpressions.Regex.Matches(content,
                    @"<ProjectReference\s+Include=""([^""]+)""");

                foreach (System.Text.RegularExpressions.Match match in projectMatches)
                {
                    var refName = Path.GetFileNameWithoutExtension(match.Groups[1].Value);
                    nodes.Add(new DiagramNode
                    {
                        Id = refName,
                        Name = refName,
                        Type = NodeType.Class
                    });
                }
            }
            else if (Directory.Exists(projectPath))
            {
                var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories);
                foreach (var csproj in csprojFiles)
                {
                    var subNodes = AnalyzeDependencies(csproj);
                    nodes.AddRange(subNodes);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ArchDiag] Error analyzing dependencies");
        }

        return nodes.DistinctBy(n => n.Id).ToList();
    }
}

public sealed class DiagramNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public NodeType Type { get; set; }
    public List<string> Dependencies { get; set; } = new();
}

public sealed class DiagramEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Label { get; set; }
}

public enum NodeType { Class, Interface, Service, Database, API, Library }
