using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class AutoFillFormTool : ToolBase
{
    private readonly IAutoFillFormService _service;
    private readonly ILogger<AutoFillFormTool> _logger;

    public override string Name => "auto_fill_form";
    public override string Description => "Remplir automatiquement un formulaire HTML avec des données. Usage: auto_fill_form(data_file: \"C:/data.json\", form_html: \"C:/formulaire.html\", output_path: \"C:/result.html\")";
    public override string Category => "web";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("data_file", "Chemin vers le fichier de données (JSON/CSV)", typeof(string), required: true),
        new ToolParameter("form_html", "Chemin vers le fichier HTML du formulaire", typeof(string), required: true),
        new ToolParameter("output_path", "Chemin du fichier de sortie", typeof(string))
    };

    public AutoFillFormTool(IAutoFillFormService service, ILogger<AutoFillFormTool> logger)
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
        var dataFile = RequireParam(parameters, "data_file");
        var formHtml = RequireParam(parameters, "form_html");
        parameters.TryGetValue("output_path", out var outputPath);

        var result = await _service.FillFormFromDataAsync(dataFile, formHtml, outputPath, ct);
        if (!result.Success)
            return Fail($"Échec du remplissage : {result.ErrorMessage}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Formulaire rempli avec succès.");
        sb.AppendLine($"Fichier de sortie : {result.OutputPath}");
        sb.AppendLine($"Champs remplis : {result.FieldsFilled}/{result.FieldsTotal}");
        if (result.UnmatchedFields.Count > 0)
        {
            sb.AppendLine("Champs non trouvés :");
            foreach (var field in result.UnmatchedFields)
                sb.AppendLine($"  - {field}");
        }

        return Ok(sb.ToString());
    }
}
