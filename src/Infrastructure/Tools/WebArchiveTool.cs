using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class WebArchiveTool : ToolBase
{
    private readonly IWebArchiveService _service;
    private readonly ILogger<WebArchiveTool> _logger;

    public override string Name => "web_archive";
    public override string Description => "Archiver une page web avec tous ses assets (CSS, JS, images). Usage: web_archive(url: \"https://example.com\", output_dir: \"C:/archive\")";
    public override string Category => "web";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("url", "URL de la page à archiver", typeof(string), required: true),
        new ToolParameter("output_dir", "Répertoire de sortie pour l'archive", typeof(string), required: true)
    };

    public WebArchiveTool(IWebArchiveService service, ILogger<WebArchiveTool> logger)
        : base(logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var url = RequireParam(parameters, "url");
        var outputDir = RequireParam(parameters, "output_dir");

        var result = await _service.ArchivePageWithAssetsAsync(url, outputDir, ct);
        if (!result.Success)
            return Fail($"Échec de l'archivage : {result.ErrorMessage}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Archive créée avec succès.");
        sb.AppendLine($"URL source : {result.Url}");
        sb.AppendLine($"Chemin index.html : {result.OutputPath}");
        sb.AppendLine($"Assets téléchargés : {result.AssetsDownloaded}");
        sb.AppendLine($"Assets échoués : {result.AssetsFailed}");
        if (result.Errors.Count > 0)
        {
            sb.AppendLine("Erreurs :");
            foreach (var error in result.Errors.Take(10))
                sb.AppendLine($"  - {error}");
        }

        return Ok(sb.ToString());
    }
}
