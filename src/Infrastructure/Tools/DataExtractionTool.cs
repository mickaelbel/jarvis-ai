using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class DataExtractionTool : ToolBase
{
    private readonly IDataExtractionService _service;
    private readonly ILogger<DataExtractionTool> _logger;

    public override string Name => "data_extraction";
    public override string Description => "Extraire un tableau HTML depuis une URL. Usage: data_extraction(url: \"https://example.com/data\") ou data_extraction(url: \"https://example.com\", format: \"xlsx\", table_index: \"1\")";
    public override string Category => "web";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("url", "URL de la page contenant le tableau", typeof(string), required: true),
        new ToolParameter("format", "Format de sortie : csv (défaut) ou xlsx", typeof(string)),
        new ToolParameter("table_index", "Index du tableau à extraire (défaut 0)", typeof(string)),
        new ToolParameter("output_path", "Chemin du fichier de sortie", typeof(string))
    };

    public DataExtractionTool(IDataExtractionService service, ILogger<DataExtractionTool> logger)
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
        parameters.TryGetValue("format", out var format);
        parameters.TryGetValue("table_index", out var tableIndexStr);
        parameters.TryGetValue("output_path", out var outputPath);

        var tableIndex = 0;
        if (int.TryParse(tableIndexStr, out var parsedIndex) && parsedIndex >= 0)
            tableIndex = parsedIndex;

        var result = await _service.ExtractTableFromUrlAsync(url, tableIndex, format ?? "csv", outputPath, ct);
        if (!result.Success)
            return Fail($"Échec de l'extraction : {result.ErrorMessage}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Tableau extrait avec succès.");
        sb.AppendLine($"Fichier : {result.OutputPath}");
        sb.AppendLine($"Lignes : {result.Rows}, Colonnes : {result.Columns}");
        if (!string.IsNullOrEmpty(result.Preview))
        {
            sb.AppendLine();
            sb.AppendLine("Aperçu :");
            sb.AppendLine(result.Preview);
        }
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            sb.AppendLine($"Note : {result.ErrorMessage}");

        return Ok(sb.ToString());
    }
}
