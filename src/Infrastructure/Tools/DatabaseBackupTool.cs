using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class DatabaseBackupTool : ToolBase
{
    private readonly IDatabaseBackupService _service;

    public override string Name => "database_backup";
    public override string Description => "Sauvegarde une base SQLite ou MySQL, liste les sauvegardes existantes et applique la rétention. Usage: database_backup(action: \"backup\", target: \"C:/data/app.db\"), database_backup(action: \"backup\", target: \"Server=localhost;Database=app;User Id=root;Password=pwd\"), database_backup(action: \"list\"), database_backup(action: \"retention\", backup_dir: \"C:/backups\", keep: \"10\").";
    public override string Category => "dev";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "backup, list, retention", typeof(string), required: true),
        new ToolParameter("target", "Chemin du fichier .db ou chaîne de connexion MySQL (requis pour backup)", typeof(string)),
        new ToolParameter("backup_dir", "Répertoire de sauvegarde (défaut %LocalAppData%\\JarvisAI\\Backups)", typeof(string)),
        new ToolParameter("keep", "Nombre de sauvegardes à conserver (défaut 10)", typeof(string)),
        new ToolParameter("mysql", "Forcer le mode MySQL (true/false, défaut : détection automatique)", typeof(string)),
    };

    public DatabaseBackupTool(IDatabaseBackupService service, ILogger<DatabaseBackupTool> logger)
        : base(logger)
    {
        _service = service;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        parameters.TryGetValue("backup_dir", out var backupDirStr);
        parameters.TryGetValue("keep", out var keepStr);
        parameters.TryGetValue("target", out var target);
        parameters.TryGetValue("mysql", out var mysqlStr);

        var backupDir = string.IsNullOrWhiteSpace(backupDirStr)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI", "Backups")
            : backupDirStr;
        var keep = int.TryParse(keepStr, out var parsedKeep) && parsedKeep >= 0 ? parsedKeep : 10;

        return action switch
        {
            "backup" => await BackupAsync(target, backupDir, mysqlStr, ct),
            "list" => await ListAsync(backupDir, ct),
            "retention" => await RetentionAsync(backupDir, keep, ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : backup, list, retention")
        };
    }

    private async Task<ToolResult> BackupAsync(string? target, string backupDir, string? mysqlStr, CancellationToken ct)
    {
        var targetPath = RequireParam(new Dictionary<string, string> { ["target"] = target ?? "" }, "target");

        bool? mysql = null;
        if (!string.IsNullOrWhiteSpace(mysqlStr))
            mysql = mysqlStr.ToLowerInvariant() is "true" or "1" or "oui" or "yes";

        var result = await _service.BackupAsync(targetPath, backupDir, mysql, ct);
        if (!result.Success)
            return Fail($"Échec de la sauvegarde : {result.ErrorMessage}");

        return Ok($"Sauvegarde {result.TargetType} créée : {result.OutputPath} ({FormatSize(result.SizeBytes)})");
    }

    private async Task<ToolResult> ListAsync(string backupDir, CancellationToken ct)
    {
        var backups = await _service.ListBackupsAsync(backupDir, ct);
        if (backups.Count == 0)
            return Ok("Aucune sauvegarde trouvée dans : " + backupDir);

        var sb = new StringBuilder();
        sb.AppendLine($"Sauvegardes dans {backupDir} ({backups.Count}) :");
        foreach (var backup in backups)
            sb.AppendLine($"  {backup.FileName} | {backup.CreatedAt:yyyy-MM-dd HH:mm:ss} | {FormatSize(backup.SizeBytes)}");
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> RetentionAsync(string backupDir, int keep, CancellationToken ct)
    {
        var removed = await _service.ApplyRetentionAsync(backupDir, keep, ct);
        return Ok($"{removed} sauvegarde(s) supprimée(s) par rétention (conservation des {keep} plus récentes).");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} Mo";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} Ko";
        return $"{bytes} o";
    }
}