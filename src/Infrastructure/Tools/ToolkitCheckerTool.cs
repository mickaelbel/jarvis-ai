using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ToolkitCheckerTool : ToolBase
{
    private readonly IToolkitVersionCheckerService _service;

    public override string Name => "toolkit_checker";
    public override string Description => "Vérifie les versions installées des outils de développement (node, npm, python, git, docker, dotnet) et signale celles qui sont obsolètes. Usage: toolkit_checker(action: \"check\").";
    public override string Category => "dev";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "check", typeof(string), required: true),
    };

    public ToolkitCheckerTool(IToolkitVersionCheckerService service, ILogger<ToolkitCheckerTool> logger)
        : base(logger)
    {
        _service = service;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        if (action != "check")
            return Fail($"Action inconnue : '{action}'. Action valide : check");

        var report = await _service.CheckAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Vérification du toolkit effectuée le {report.CheckedAt:yyyy-MM-dd HH:mm:ss} UTC.");
        sb.AppendLine($"Outils installés : {report.InstalledCount}/{report.Tools.Count} | obsolètes : {report.OutdatedCount}");
        sb.AppendLine();

        foreach (var tool in report.Tools)
        {
            var status = tool.Installed ? $"v{tool.ActualVersion}" : "non installé";
            var latest = string.IsNullOrEmpty(tool.LatestKnownVersion) || tool.LatestKnownVersion == "inconnue" ? "" : $" (dernière connue : {tool.LatestKnownVersion})";
            sb.AppendLine($"  {tool.Name} : {status}{latest}");
            if (!string.IsNullOrWhiteSpace(tool.UpdateProposal))
                sb.AppendLine($"    → {tool.UpdateProposal}");
        }

        return Ok(sb.ToString());
    }
}