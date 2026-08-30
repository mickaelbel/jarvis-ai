using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ProjectScaffoldingTool : ITool
{
    private readonly ILogger<ProjectScaffoldingTool> _logger;

    public string Name => "project_scaffold";
    public string Description => "Créer de nouveaux projets depuis des templates (console, web API, blazor, maui, library)";
    public string Category => "Development";
    public bool IsReadOnly => false;

    public ProjectScaffoldingTool(ILogger<ProjectScaffoldingTool> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "list_templates, create, list_files", typeof(string), required: true),
        new ToolParameter("template", "console, webapi, blazor, maui, library", typeof(string)),
        new ToolParameter("name", "Nom du projet", typeof(string)),
        new ToolParameter("output_path", "Chemin de sortie", typeof(string)),
        new ToolParameter("framework", "net8.0, net9.0", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var action = parameters.GetValueOrDefault("action") ?? "list_templates";

        try
        {
            return action switch
            {
                "list_templates" => ListTemplates(),
                "create" => await CreateProject(parameters, ct),
                "list_files" => ListTemplateFiles(parameters),
                _ => ToolResult.Failed($"Action inconnue: {action}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProjectScaffold] Error");
            return ToolResult.Failed($"Erreur: {ex.Message}");
        }
    }

    private ToolResult ListTemplates()
    {
        var templates = new StringBuilder();
        templates.AppendLine("Templates disponibles:");
        templates.AppendLine("  console       - Application console .NET");
        templates.AppendLine("  webapi        - API Web ASP.NET Core");
        templates.AppendLine("  blazor        - Application Blazor Server");
        templates.AppendLine("  blazorwasm    - Application Blazor WebAssembly");
        templates.AppendLine("  maui          - Application .NET MAUI");
        templates.AppendLine("  library       - Bibliothèque de classes");
        templates.AppendLine("  mvc           - Application MVC ASP.NET Core");
        templates.AppendLine("  worker        - Service worker .NET");

        return ToolResult.Succeeded(templates.ToString());
    }

    private async Task<ToolResult> CreateProject(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var template = parameters.GetValueOrDefault("template") ?? "console";
        var name = parameters.GetValueOrDefault("name") ?? "MyProject";
        var outputPath = parameters.GetValueOrDefault("output_path") ?? Path.Combine(Directory.GetCurrentDirectory(), name);
        var framework = parameters.GetValueOrDefault("framework") ?? "net8.0";

        var templateMap = new Dictionary<string, string>
        {
            ["console"] = "console",
            ["webapi"] = "webapi",
            ["blazor"] = "blazorserver",
            ["blazorwasm"] = "blazorwasm",
            ["maui"] = "maui",
            ["library"] = "classlib",
            ["mvc"] = "mvc",
            ["worker"] = "worker"
        };

        if (!templateMap.TryGetValue(template, out var templateId))
            return ToolResult.Failed($"Template inconnu: {template}. Utilisez 'list_templates'.");

        var sb = new StringBuilder();
        sb.AppendLine($"Création du projet '{name}' (template: {template}, framework: {framework})...");

        // Create directory
        if (!Directory.Exists(outputPath))
            Directory.CreateDirectory(outputPath);

        // Create project file
        var projectFile = templateId switch
        {
            "console" => CreateConsoleProject(name, framework),
            "webapi" => CreateWebApiProject(name, framework),
            "blazorserver" => CreateBlazorProject(name, framework),
            "classlib" => CreateLibraryProject(name, framework),
            _ => CreateConsoleProject(name, framework)
        };

        await File.WriteAllTextAsync(Path.Combine(outputPath, $"{name}.csproj"), projectFile, ct);

        // Create Program.cs
        var programContent = templateId switch
        {
            "webapi" => CreateWebApiProgram(),
            "blazorserver" => CreateBlazorProgram(),
            _ => CreateConsoleProgram()
        };

        await File.WriteAllTextAsync(Path.Combine(outputPath, "Program.cs"), programContent, ct);

        // Create launchSettings.json for web templates
        if (templateId is "webapi" or "blazorserver" or "mvc")
        {
            var launchSettings = CreateLaunchSettings(name);
            var propertiesDir = Path.Combine(outputPath, "Properties");
            Directory.CreateDirectory(propertiesDir);
            await File.WriteAllTextAsync(Path.Combine(propertiesDir, "launchSettings.json"), launchSettings, ct);
        }

        sb.AppendLine($"Projet créé avec succès dans: {outputPath}");
        sb.AppendLine($"Fichiers créés: {name}.csproj, Program.cs");

        return ToolResult.Succeeded(sb.ToString());
    }

    private ToolResult ListTemplateFiles(IReadOnlyDictionary<string, string> parameters)
    {
        var template = parameters.GetValueOrDefault("template") ?? "console";
        var files = template switch
        {
            "console" => new[] { "Program.cs", $"{template}.csproj", ".gitignore" },
            "webapi" => new[] { "Program.cs", "appsettings.json", "Properties/launchSettings.json", $"{template}.csproj" },
            "blazor" => new[] { "Program.cs", "Pages/", "Shared/", "_Imports.razor", $"{template}.csproj" },
            _ => new[] { "Program.cs", $"{template}.csproj" }
        };

        return ToolResult.Succeeded($"Fichiers pour {template}: {string.Join(", ", files)}");
    }

    private string CreateConsoleProject(string name, string framework) => $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>{framework}</TargetFramework>
    <RootNamespace>{name}</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>";

    private string CreateWebApiProject(string name, string framework) => $@"<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <PropertyGroup>
    <TargetFramework>{framework}</TargetFramework>
    <RootNamespace>{name}</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>";

    private string CreateBlazorProject(string name, string framework) => $@"<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <PropertyGroup>
    <TargetFramework>{framework}</TargetFramework>
    <RootNamespace>{name}</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>";

    private string CreateLibraryProject(string name, string framework) => $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>{framework}</TargetFramework>
    <RootNamespace>{name}</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>";

    private string CreateConsoleProgram() => "Console.WriteLine(\"Hello, World!\");";

    private string CreateWebApiProgram() => @"var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.Run();";

    private string CreateBlazorProgram() => @"var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage(""/_Host"");

app.Run();";

    private string CreateLaunchSettings(string name) => $@"{{
  ""profiles"": {{
    ""{name}"": {{
      ""commandName"": ""Project"",
      ""dotnetRunMessages"": true,
      ""launchBrowser"": true,
      ""applicationUrl"": ""https://localhost:7200;http://localhost:5200"",
      ""environmentVariables"": {{
        ""ASPNETCORE_ENVIRONMENT"": ""Development""
      }}
    }}
  }}
}}";
}
