using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class HttpLoadTestTool : ToolBase
{
    private readonly ILoadTestService _service;

    public override string Name => "http_load_test";
    public override string Description => "Lance un test de charge HTTP sur une URL et renvoie les statistiques de performance (requêtes, échecs, débit, latences et codes de statut). Usage: http_load_test(url: \"https://example.com\") ou http_load_test(url: \"https://example.com\", requests: \"1000\", concurrency: \"50\").";
    public override string Category => "dev";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(10);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("url", "URL à tester", typeof(string), required: true),
        new ToolParameter("requests", "Nombre total de requêtes (défaut 1000)", typeof(string)),
        new ToolParameter("concurrency", "Concurrence maximale (défaut 50)", typeof(string)),
    };

    public HttpLoadTestTool(ILoadTestService service, ILogger<HttpLoadTestTool> logger)
        : base(logger)
    {
        _service = service;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var url = RequireParam(parameters, "url");
        parameters.TryGetValue("requests", out var requestsStr);
        parameters.TryGetValue("concurrency", out var concurrencyStr);

        var requests = int.TryParse(requestsStr, out var parsedRequests) && parsedRequests > 0 ? parsedRequests : 1000;
        var concurrency = int.TryParse(concurrencyStr, out var parsedConcurrency) && parsedConcurrency > 0 ? parsedConcurrency : 50;

        var result = await _service.RunAsync(url, requests, concurrency, ct);
        if (!result.Success && result.Failed > 0 && result.Succeeded == 0)
            return Fail($"Échec du test de charge : {result.ErrorMessage}");

        var sb = new StringBuilder();
        sb.AppendLine($"Test de charge terminé : {result.Url}");
        sb.AppendLine($"Requêtes : {result.TotalRequests} | réussies : {result.Succeeded}, échecs : {result.Failed}");
        sb.AppendLine($"Débit : {result.RequestsPerSecond:F1} req/s");
        sb.AppendLine($"Latence (ms) : min {result.MinLatencyMs:F0} | moy {result.AverageLatencyMs:F0} | max {result.MaxLatencyMs:F0} | p50 {result.P50:F0} | p90 {result.P90:F0} | p95 {result.P95:F0} | p99 {result.P99:F0}");
        sb.AppendLine("Top codes de statut :");
        foreach (var kv in result.StatusCodes.OrderByDescending(kvp => kvp.Value).Take(10))
            sb.AppendLine($"  {kv.Key} : {kv.Value}");
        if (result.Errors.Count > 0)
        {
            sb.AppendLine($"Erreurs ({result.Errors.Count}, aperçu 5) :");
            foreach (var err in result.Errors.Take(5))
                sb.AppendLine($"  {err}");
        }

        return Ok(sb.ToString());
    }
}