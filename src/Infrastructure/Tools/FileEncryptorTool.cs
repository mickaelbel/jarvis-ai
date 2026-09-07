using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class FileEncryptorTool : ToolBase
{
    private readonly IFileEncryptorService _service;
    private readonly ILogger<FileEncryptorTool> _logger;

    public override string Name => "file_encryptor";
    public override string Description => "Chiffrer/déchiffrer des fichiers ou dossiers avec un mot de passe. Usage: file_encryptor(action: \"encrypt\", path: \"C:/secret.txt\", password: \"mdp\", output_path: \"C:/secret.encrypted\"). Actions : encrypt, decrypt. Attention : le mot de passe est transmis au modèle — risque élevé.";
    public override string Category => "files";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(30);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "encrypt ou decrypt (requis)", typeof(string), required: true),
        new ToolParameter("path", "Fichier ou dossier à traiter (requis)", typeof(string), required: true),
        new ToolParameter("password", "Mot de passe de chiffrement (requis)", typeof(string), required: true),
        new ToolParameter("output_path", "Chemin du fichier de sortie", typeof(string))
    };

    public FileEncryptorTool(IFileEncryptorService service, ILogger<FileEncryptorTool> logger)
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
        var path = RequireParam(parameters, "path");
        var password = RequireParam(parameters, "password");
        parameters.TryGetValue("output_path", out var outputPath);

        var isFolder = Directory.Exists(path);
        if (!isFolder && !File.Exists(path))
            return Fail($"Chemin introuvable : {path}");

        if (isFolder)
        {
            var result = action switch
            {
                "encrypt" => await _service.EncryptFolderAsync(path, password, ct),
                "decrypt" => await _service.DecryptFolderAsync(path, password, ct),
                _ => null
            };
            if (result is null)
                return Fail($"Action inconnue : '{action}'. Actions valides : encrypt, decrypt.");
            if (!result.Success)
                return Fail($"Échec : {result.ErrorMessage}");
            return Ok($"Dossier {(action == "encrypt" ? "chiffré" : "déchiffré")} avec succès.\nDossier : {path}\nFichiers traités : {result.FilesProcessed}");
        }

        var fileResult = action switch
        {
            "encrypt" => await _service.EncryptFileAsync(path, password, outputPath, ct),
            "decrypt" => await _service.DecryptFileAsync(path, password, outputPath, ct),
            _ => null
        };
        if (fileResult is null)
            return Fail($"Action inconnue : '{action}'. Actions valides : encrypt, decrypt.");
        if (!fileResult.Success)
            return Fail($"Échec : {fileResult.ErrorMessage}");

        return Ok($"Fichier {(action == "encrypt" ? "chiffré" : "déchiffré")} avec succès.\nSortie : {fileResult.OutputPath}\nTaille : {FormatSize(fileResult.OutputSizeBytes)}");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }
}
