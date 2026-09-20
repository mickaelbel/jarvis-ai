using JarvisAI.Application.Agents;
using JarvisAI.Application.Biometry;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Face recognition tool for the agent.
/// Actions: recognize, enroll, list, forget, describe.
/// </summary>
public sealed class FaceTool : ITool
{
    private readonly IFaceRecognitionService _faceRec;
    private readonly ILogger<FaceTool> _logger;

    public FaceTool(IFaceRecognitionService faceRec, ILogger<FaceTool> logger)
    {
        _faceRec = faceRec;
        _logger = logger;
    }

    public string Name => "reconnaissance_faciale";
    public string Description =>
        "Reconnaissance faciale via webcam : identifier les personnes, enregistrer un nouveau visage, " +
        "lister les visages enregistres, oublier un visage, decrire la scene. " +
        "Actions : recognize, enroll <nom>, list, forget <id|nom>, describe.";
    public string Category => "biometry";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "recognize | enroll | list | forget | describe", typeof(string), required: true),
        new("nom", "Nom de la personne a enregistrer (pour enroll)", typeof(string), required: false),
        new("id", "ID ou nom du visage a oublier (pour forget)", typeof(string), required: false),
        new("notes", "Notes optionnelles sur la personne", typeof(string), required: false)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        if (!_faceRec.IsAvailable)
            return ToolResult.Failed("Reconnaissance faciale indisponible. Verifiez que la webcam est connectee et que les modeles sont charges.");

        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("nom", out var nom);
        parameters.TryGetValue("id", out var id);
        parameters.TryGetValue("notes", out var notes);

        try
        {
            return (action?.ToLowerInvariant().Trim()) switch
            {
                "recognize" => await Recognize(ct),
                "enroll" => await Enroll(nom, notes, ct),
                "list" => List(),
                "forget" => Forget(id),
                "describe" => await Describe(ct),
                _ => ToolResult.Failed("Action inconnue : recognize, enroll, list, forget, describe.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FaceTool] Error executing action {Action}", action);
            return ToolResult.Failed($"Erreur : {ex.Message}");
        }
    }

    private async Task<ToolResult> Recognize(CancellationToken ct)
    {
        var result = await _faceRec.RecognizeAsync(ct);
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            return ToolResult.Failed(result.ErrorMessage);

        var msg = result.FaceCount == 0
            ? "Aucun visage detecte."
            : $"Visage(s) detecte(s) : {result.FaceCount}";

        if (!string.IsNullOrEmpty(result.DetectedName) && result.DetectedName != "Inconnu")
            msg += $"\nPersonne reconnue : {result.DetectedName} (confiance: {result.Confidence:P0})";

        if (!string.IsNullOrEmpty(result.Notes))
            msg += $"\n{result.Notes}";

        return ToolResult.Succeeded(msg);
    }

    private async Task<ToolResult> Enroll(string? nom, string? notes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nom))
            return ToolResult.Failed("Precisez le nom de la personne a enregistrer.");

        var result = await _faceRec.EnrollAsync(nom, notes, ct);
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            return ToolResult.Failed(result.ErrorMessage);

        return ToolResult.Succeeded($"{result.Notes}. ID: {result.FaceId}");
    }

    private ToolResult List()
    {
        var enrolled = _faceRec.ListEnrolled();
        if (enrolled.Count == 0)
            return ToolResult.Succeeded("Aucun visage enregistre.");

        var lines = enrolled.Select(f =>
            $"[{f.Id}] {f.Name}" +
            (string.IsNullOrEmpty(f.Notes) ? "" : $" - {f.Notes}") +
            $" (enregistre le {f.EnrolledAt:dd/MM/yyyy})");
        return ToolResult.Succeeded(string.Join("\n", lines));
    }

    private ToolResult Forget(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Failed("Precisez l'ID ou le nom du visage a oublier.");

        var ok = _faceRec.Forget(id);
        return ok
            ? ToolResult.Succeeded($"Visage '{id}' oublie.")
            : ToolResult.Failed($"Visage '{id}' introuvable.");
    }

    private async Task<ToolResult> Describe(CancellationToken ct)
    {
        var desc = await _faceRec.DescribeSceneAsync(ct);
        return ToolResult.Succeeded(desc);
    }
}
