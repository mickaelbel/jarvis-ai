using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class DocumentTranslatorTool : ToolBase
{
    private readonly IDocumentTranslatorService _service;
    private readonly ILogger<DocumentTranslatorTool> _logger;

    public override string Name => "document_translator";
    public override string Description => "Traduire un document (txt, docx, pdf) vers une autre langue. Usage: document_translator(file_path: \"C:/doc.txt\", target_language: \"en\", output_path: \"C:/doc_en.txt\"). Langues : fr, en, es, de, it, pt, nl, ru, zh, ja, ko, ar.";
    public override string Category => "files";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("file_path", "Chemin du fichier à traduire", typeof(string), required: true),
        new ToolParameter("target_language", "Langue cible (code 2 lettres, ex: en, es)", typeof(string), required: true),
        new ToolParameter("output_path", "Chemin du fichier de sortie", typeof(string))
    };

    public DocumentTranslatorTool(IDocumentTranslatorService service, ILogger<DocumentTranslatorTool> logger)
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
        var filePath = RequireParam(parameters, "file_path");
        var targetLanguage = RequireParam(parameters, "target_language").ToLowerInvariant();
        parameters.TryGetValue("output_path", out var outputPath);

        var result = await _service.TranslateFileAsync(filePath, targetLanguage, outputPath, ct);
        if (!result.Success)
            return Fail($"Échec de la traduction : {result.ErrorMessage}");

        return Ok($"Traduction terminée.\nFichier d'entrée : {result.InputPath}\nFichier de sortie : {result.OutputPath}\nMots traduits : {result.WordsTranslated}\nLangue cible : {result.TargetLanguage}");
    }
}
