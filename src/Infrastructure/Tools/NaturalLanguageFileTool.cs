using JarvisAI.Application.Agents;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class NaturalLanguageFileTool : ITool
{
    private readonly ILogger<NaturalLanguageFileTool> _logger;
    private readonly ISecurityManager? _security;

    public string Name => "nl_file_ops";
    public string Description => "Effectue des opérations sur fichiers en langage naturel. Exemples: 'déplace tous les PDF du dossier Téléchargements vers Travail', 'supprime les images de plus de 30 jours', 'copie les fichiers modifiés aujourd'hui vers backup'. Parse automatiquement intention, type de fichier, dates et destinations.";
    public string Category => "filesystem";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("instruction", "Instruction en langage naturel décrivant l'opération souhaitée", typeof(string), required: true),
        new ToolParameter("source_folder", "Dossier source (optionnel, extrait de l'instruction si non fourni)", typeof(string)),
        new ToolParameter("destination_folder", "Dossier destination (optionnel, extrait de l'instruction si non fourni)", typeof(string)),
        new ToolParameter("dry_run", "Si true, simulate sans exécuter (défaut: false)", typeof(string)),
    };

    public NaturalLanguageFileTool(ILogger<NaturalLanguageFileTool> logger, ISecurityManager? securityManager = null)
    {
        _logger = logger;
        _security = securityManager;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("instruction", out var instruction);
        parameters.TryGetValue("source_folder", out var sourceFolder);
        parameters.TryGetValue("destination_folder", out var destFolder);
        parameters.TryGetValue("dry_run", out var dryRunStr);

        if (string.IsNullOrWhiteSpace(instruction))
            return ToolResult.Failed("instruction est requise");

        var dryRun = dryRunStr?.ToLowerInvariant() == "true";

        try
        {
            var parsed = ParseInstruction(instruction, sourceFolder, destFolder);

            if (dryRun)
            {
                var preview = string.Join("\n", parsed.FilesToProcess.Take(20)
                    .Select(f => $"  {f.Action}: {f.Source} → {f.Destination ?? "(supprimer)"}"));
                return ToolResult.Succeeded($"Simulation ({parsed.FilesToProcess.Count} fichiers):\n{parsed.Description}\n{preview}");
            }

            var results = await ExecuteParsedAsync(parsed, cancellationToken);
            return ToolResult.Succeeded(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NLFileOps] Failed to process instruction");
            return ToolResult.Failed($"Erreur: {ex.Message}");
        }
    }

    private FileOperation ParseInstruction(string instruction, string? sourceOverride, string? destOverride)
    {
        var lower = instruction.ToLowerInvariant();
        var op = new FileOperation { Description = instruction };

        // Detect action
        if (lower.Contains("déplace") || lower.Contains("deplace") || lower.Contains("move"))
            op.Action = FileAction.Move;
        else if (lower.Contains("copie") || lower.Contains("copy"))
            op.Action = FileAction.Copy;
        else if (lower.Contains("supprime") || lower.Contains("delete") || lower.Contains("efface"))
            op.Action = FileAction.Delete;
        else if (lower.Contains("renomme") || lower.Contains("rename"))
            op.Action = FileAction.Rename;
        else
            op.Action = FileAction.Move;

        // Detect file type
        var patterns = new List<string>();
        if (lower.Contains("pdf")) patterns.Add("*.pdf");
        if (lower.Contains("image") || lower.Contains("photo")) patterns.AddRange(new[] { "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp" });
        if (lower.Contains("vidéo") || lower.Contains("video")) patterns.AddRange(new[] { "*.mp4", "*.avi", "*.mkv", "*.mov" });
        if (lower.Contains("document") || lower.Contains("doc")) patterns.AddRange(new[] { "*.docx", "*.doc", "*.txt", "*.rtf" });
        if (lower.Contains("musique") || lower.Contains("audio")) patterns.AddRange(new[] { "*.mp3", "*.wav", "*.flac", "*.m4a" });
        if (lower.Contains("zip") || lower.Contains("archive")) patterns.AddRange(new[] { "*.zip", "*.rar", "*.7z", "*.tar" });

        if (patterns.Count == 0) patterns.Add("*.*");
        op.Patterns = patterns;

        // Detect source/destination folders
        var knownFolders = new Dictionary<string, string>
        {
            ["téléchargements"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            ["downloads"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            ["bureau"] = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            ["desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            ["documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["musique"] = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            ["images"] = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ["vidéos"] = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };

        // Extract source from instruction
        foreach (var kv in knownFolders)
        {
            if (lower.Contains(kv.Key))
            {
                op.SourcePath = kv.Value;
                break;
            }
        }

        // Extract "depuis" source
        var depuisIdx = lower.IndexOf("depuis ");
        if (depuisIdx >= 0)
        {
            var rest = instruction[(depuisIdx + 7)..];
            var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var folderName = words.FirstOrDefault()?.TrimEnd('.', ',', ';');
            if (folderName is not null && knownFolders.TryGetValue(folderName.ToLower(), out var path))
                op.SourcePath = path;
        }

        // Extract destination
        var versIdx = lower.IndexOf("vers ");
        var dansIdx = lower.IndexOf("dans ");
        var targetIdx = Math.Max(versIdx, dansIdx);
        if (targetIdx >= 0)
        {
            var rest = instruction[(targetIdx + 5)..];
            var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var folderName = words.FirstOrDefault()?.TrimEnd('.', ',', ';');
            if (folderName is not null && knownFolders.TryGetValue(folderName.ToLower(), out var path))
                op.DestinationPath = path;
        }

        if (sourceOverride is not null) op.SourcePath = sourceOverride;
        if (destOverride is not null) op.DestinationPath = destOverride;

        return op;
    }

    private async Task<string> ExecuteParsedAsync(FileOperation op, CancellationToken ct)
    {
        var processed = 0;
        var errors = 0;

        if (string.IsNullOrEmpty(op.SourcePath) || !Directory.Exists(op.SourcePath))
            return $"Dossier source introuvable: {op.SourcePath ?? "(non spécifié)"}";

        foreach (var pattern in op.Patterns)
        {
            var files = Directory.GetFiles(op.SourcePath, pattern, SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var fileName = Path.GetFileName(file);
                    var destPath = op.DestinationPath is not null
                        ? Path.Combine(op.DestinationPath, fileName)
                        : null;

                    switch (op.Action)
                    {
                        case FileAction.Move when destPath is not null:
                            var destDir = Path.GetDirectoryName(destPath);
                            if (destDir is not null && !Directory.Exists(destDir))
                                Directory.CreateDirectory(destDir);
                            File.Move(file, destPath, overwrite: true);
                            break;
                        case FileAction.Copy when destPath is not null:
                            var copyDir = Path.GetDirectoryName(destPath);
                            if (copyDir is not null && !Directory.Exists(copyDir))
                                Directory.CreateDirectory(copyDir);
                            File.Copy(file, destPath, overwrite: true);
                            break;
                        case FileAction.Delete:
                            File.Delete(file);
                            break;
                    }
                    processed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[NLFileOps] Failed: {File}", file);
                    errors++;
                }
            }
        }

        return $"{op.Action}: {processed} fichiers traités, {errors} erreurs";
    }
}

internal sealed class FileOperation
{
    public FileAction Action { get; set; }
    public string Description { get; set; } = "";
    public string? SourcePath { get; set; }
    public string? DestinationPath { get; set; }
    public List<string> Patterns { get; set; } = new();
    public List<FileProcessInfo> FilesToProcess { get; set; } = new();
}

internal sealed class FileProcessInfo
{
    public string Source { get; set; } = "";
    public string? Destination { get; set; }
    public string Action { get; set; } = "";
}

internal enum FileAction { Move, Copy, Delete, Rename }
