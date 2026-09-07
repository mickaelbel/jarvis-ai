using Microsoft.Extensions.Logging;
using System.Net;
using System.Reflection;
using System.Text;

namespace JarvisAI.Web.Services;

public interface IApiDocumentationGenerator
{
    string GenerateDocumentation(Type apiType);
    string GenerateDocumentationHtml(Type apiType);
    string GenerateOpenApiSpec(Type apiType);
    IReadOnlyList<ApiEndpoint> GetEndpoints(Type apiType);
}

public sealed class ApiDocumentationGenerator : IApiDocumentationGenerator
{
    private readonly ILogger<ApiDocumentationGenerator> _logger;

    public ApiDocumentationGenerator(ILogger<ApiDocumentationGenerator> logger)
    {
        _logger = logger;
    }

    public string GenerateDocumentation(Type apiType)
    {
        var endpoints = GetEndpoints(apiType);
        var sb = new StringBuilder();

        sb.AppendLine($"# Documentation API - {apiType.Name}");
        sb.AppendLine();
        sb.AppendLine("## Endpoints");
        sb.AppendLine();

        foreach (var endpoint in endpoints)
        {
            sb.AppendLine($"### {endpoint.Method} {endpoint.Path}");
            sb.AppendLine();
            sb.AppendLine($"**Description:** {endpoint.Description}");
            sb.AppendLine();

            if (endpoint.Parameters.Any())
            {
                sb.AppendLine("**Paramètres:**");
                sb.AppendLine();
                foreach (var param in endpoint.Parameters)
                {
                    sb.AppendLine($"- `{param.Name}` ({param.Type}){(param.Required ? " *requis*" : "")} - {param.Description}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string GenerateDocumentationHtml(Type apiType)
    {
        var endpoints = GetEndpoints(apiType);
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"fr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>Documentation API - " + WebUtility.HtmlEncode(apiType.Name) + "</title>");
        sb.AppendLine("<link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css\" rel=\"stylesheet\">");
        sb.AppendLine("</head>");
        sb.AppendLine("<body class=\"bg-light\">");
        sb.AppendLine("<div class=\"container py-4\">");
        sb.AppendLine("<h1 class=\"mb-4\">Documentation API - " + WebUtility.HtmlEncode(apiType.Name) + "</h1>");
        sb.AppendLine("<table class=\"table table-striped table-hover bg-white\">");
        sb.AppendLine("<thead class=\"table-dark\"><tr><th>Méthode</th><th>Chemin</th><th>Description</th><th>Paramètres</th></tr></thead>");
        sb.AppendLine("<tbody>");

        foreach (var endpoint in endpoints)
        {
            var parameterCells = endpoint.Parameters.Select(p =>
                $"{WebUtility.HtmlEncode(p.Name)} ({WebUtility.HtmlEncode(p.Type)}){(p.Required ? " <em>*requis*</em>" : "")} - {WebUtility.HtmlEncode(p.Description)}");

            sb.AppendLine("<tr>");
            sb.AppendLine($"<td><span class=\"badge bg-primary\">{WebUtility.HtmlEncode(endpoint.Method)}</span></td>");
            sb.AppendLine($"<td><code>{WebUtility.HtmlEncode(endpoint.Path)}</code></td>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(endpoint.Description)}</td>");
            sb.AppendLine($"<td>{string.Join("<br>", parameterCells)}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    public string GenerateOpenApiSpec(Type apiType)
    {
        var endpoints = GetEndpoints(apiType);
        var sb = new StringBuilder();

        sb.AppendLine("openapi: 3.0.0");
        sb.AppendLine("info:");
        sb.AppendLine($"  title: {apiType.Name} API");
        sb.AppendLine("  version: 1.0.0");
        sb.AppendLine("paths:");

        foreach (var endpoint in endpoints)
        {
            sb.AppendLine($"  {endpoint.Path}:");
            sb.AppendLine($"    {endpoint.Method.ToLowerInvariant()}:");
            sb.AppendLine($"      summary: {endpoint.Description}");
        }

        return sb.ToString();
    }

    public IReadOnlyList<ApiEndpoint> GetEndpoints(Type apiType)
    {
        var endpoints = new List<ApiEndpoint>();

        var methods = apiType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            if (method.Name.StartsWith("Get") || method.Name.StartsWith("Post") ||
                method.Name.StartsWith("Put") || method.Name.StartsWith("Delete"))
            {
                var endpoint = new ApiEndpoint
                {
                    Method = method.Name.StartsWith("Get") ? "GET" :
                            method.Name.StartsWith("Post") ? "POST" :
                            method.Name.StartsWith("Put") ? "PUT" : "DELETE",
                    Path = $"/{method.Name.ToLowerInvariant().Replace("get", "").Replace("post", "").Replace("put", "").Replace("delete", "")}",
                    Description = method.Name,
                    ReturnType = method.ReturnType.Name
                };

                var parameters = method.GetParameters();
                foreach (var param in parameters)
                {
                    endpoint.Parameters.Add(new ApiParameter
                    {
                        Name = param.Name ?? "",
                        Type = param.ParameterType.Name,
                        Description = param.Name ?? "",
                        Required = !param.IsOptional
                    });
                }

                endpoints.Add(endpoint);
            }
        }

        return endpoints;
    }
}

public sealed class ApiEndpoint
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public List<ApiParameter> Parameters { get; set; } = new();
}

public sealed class ApiParameter
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Required { get; set; }
}
