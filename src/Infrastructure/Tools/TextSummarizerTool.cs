using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class TextSummarizerTool : ToolBase
{
    private readonly ITextSummarizerService _service;
    private readonly ILogger<TextSummarizerTool> _logger;

    public override string Name => "text_summarizer";
    public override string Description => "Résumer un texte ou un fichier texte. Usage: text_summarizer(text: \"...\") ou text_summarizer(file_path: \"C:/doc.txt\", max_words: \"200\")";
    public override string Category => "text";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("text", "Texte à résumer (si pas de file_path)", typeof(string)),
        new ToolParameter("file_path", "Chemin d'un fichier texte à résumer", typeof(string)),
        new ToolParameter("max_words", "Nombre max de mots du résumé (défaut 200)", typeof(string))
    };

    public TextSummarizerTool(ITextSummarizerService service, ILogger<TextSummarizerTool> logger)
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
        parameters.TryGetValue("text", out var text);
        parameters.TryGetValue("file_path", out var filePath);
        parameters.TryGetValue("max_words", out var maxWordsStr);

        var maxWords = 200;
        if (int.TryParse(maxWordsStr, out var parsed) && parsed > 0)
            maxWords = parsed;

        string content;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
                return Fail($"Fichier introuvable : {filePath}");
            content = await File.ReadAllTextAsync(filePath, ct);
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            content = text;
        }
        else
        {
            return Fail("Fournissez 'text' ou 'file_path'.");
        }

        var summary = await _service.SummarizeAsync(content, maxWords, ct);
        if (string.IsNullOrWhiteSpace(summary))
            return Fail("Aucun résumé généré (texte trop court ou vide).");

        return Ok($"Résumé ({maxWords} mots max) :\n\n{summary}");
    }
}
