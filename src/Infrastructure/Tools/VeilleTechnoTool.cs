using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class VeilleTechnoTool : ToolBase
{
    private readonly IVeilleTechnoService _service;
    private readonly ILogger<VeilleTechnoTool> _logger;

    public override string Name => "veille_techno";
    public override string Description => "Veille technologique : collecter, tendances, rapport, sources. Usage: veille_techno(action: \"collect\") ou veille_techno(action: \"trending\", source: \"github\", limit: \"20\"). Actions : collect, trending, report, sources.";
    public override string Category => "web";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "collect, trending, report, sources (requis)", typeof(string), required: true),
        new ToolParameter("source", "Source pour trending (défaut : github)", typeof(string)),
        new ToolParameter("limit", "Nombre d'items à récupérer (défaut 20)", typeof(string)),
        new ToolParameter("topics", "Sujets supplémentaires (pour plus tard)", typeof(string))
    };

    public VeilleTechnoTool(IVeilleTechnoService service, ILogger<VeilleTechnoTool> logger)
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
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        parameters.TryGetValue("source", out var source);
        parameters.TryGetValue("limit", out var limitStr);

        var limit = 20;
        if (int.TryParse(limitStr, out var parsedLimit) && parsedLimit > 0)
            limit = parsedLimit;

        return action switch
        {
            "sources" => ListSources(),
            "collect" => await CollectAsync(ct),
            "trending" => await TrendingAsync(source ?? "github", limit, ct),
            "report" => await ReportAsync(ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : collect, trending, report, sources")
        };
    }

    private ToolResult ListSources()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Sources disponibles :");
        foreach (var source in VeilleTechnoService.DefaultSources)
            sb.AppendLine($"  - {source}");
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> CollectAsync(CancellationToken ct)
    {
        var result = await _service.CollectAsync(VeilleTechnoService.DefaultSources, ct);
        if (!result.Success)
            return Fail("Échec de la collecte.");

        var report = await _service.GenerateReportAsync(result.Items, ct);
        return Ok($"Collecte terminée : {result.Items.Count} items depuis {result.SourcesCollected} sources.\n\n{report}");
    }

    private async Task<ToolResult> TrendingAsync(string source, int limit, CancellationToken ct)
    {
        var items = await _service.GetTrendingAsync(source, limit, ct);
        if (items.Count == 0)
            return Ok($"Aucun item tendance trouvé pour « {source} ».");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Tendances « {source} » ({items.Count} items) :");
        foreach (var item in items)
        {
            sb.AppendLine($"  - [{item.Title}]({item.Url}) (score: {item.Score})");
        }
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> ReportAsync(CancellationToken ct)
    {
        var result = await _service.CollectAsync(VeilleTechnoService.DefaultSources, ct);
        var report = await _service.GenerateReportAsync(result.Items, ct);
        return Ok(report);
    }
}
