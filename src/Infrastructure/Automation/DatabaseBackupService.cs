using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface IDatabaseBackupService
{
    Task<DatabaseBackupResult> BackupAsync(string target, string backupDir, bool? mysql = null, CancellationToken ct = default);
    Task<int> ApplyRetentionAsync(string backupDir, int keepCount = 10, CancellationToken ct = default);
    Task<List<DatabaseBackupInfo>> ListBackupsAsync(string backupDir, CancellationToken ct = default);
}

public sealed class DatabaseBackupService : IDatabaseBackupService
{
    private readonly ILogger<DatabaseBackupService> _logger;

    public DatabaseBackupService(ILogger<DatabaseBackupService> logger)
    {
        _logger = logger;
    }

    public async Task<DatabaseBackupResult> BackupAsync(string target, string backupDir, bool? mysql = null, CancellationToken ct = default)
    {
        var result = new DatabaseBackupResult();

        try
        {
            Directory.CreateDirectory(backupDir);

            bool isMySql = mysql ?? DetectIsMySql(target);

            if (isMySql)
            {
                result.TargetType = "mysql";
                return await BackupMySqlAsync(target, backupDir, ct);
            }
            else
            {
                result.TargetType = "sqlite";
                return await BackupSqliteAsync(target, backupDir, ct);
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[DatabaseBackup] Erreur lors de la sauvegarde de {Target}", target);
            return result;
        }
    }

    public async Task<int> ApplyRetentionAsync(string backupDir, int keepCount = 10, CancellationToken ct = default)
    {
        if (!Directory.Exists(backupDir)) return 0;

        var files = Directory.GetFiles(backupDir, "db_*")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();

        int removed = 0;
        foreach (var file in files.Skip(keepCount))
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                file.Delete();
                removed++;
                _logger.LogInformation("[DatabaseBackup] Fichier supprimé (rétention) : {File}", file.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DatabaseBackup] Impossible de supprimer {File}", file.Name);
            }
        }

        return removed;
    }

    public Task<List<DatabaseBackupInfo>> ListBackupsAsync(string backupDir, CancellationToken ct = default)
    {
        var backups = new List<DatabaseBackupInfo>();

        if (!Directory.Exists(backupDir))
            return Task.FromResult(backups);

        foreach (var file in Directory.GetFiles(backupDir, "db_*"))
        {
            try
            {
                var fi = new FileInfo(file);
                backups.Add(new DatabaseBackupInfo
                {
                    FileName = fi.Name,
                    SizeBytes = fi.Length,
                    CreatedAt = fi.CreationTimeUtc,
                    FilePath = fi.FullName
                });
            }
            catch { }
        }

        backups = backups.OrderByDescending(b => b.CreatedAt).ToList();
        return Task.FromResult(backups);
    }

    private static bool DetectIsMySql(string target)
    {
        if (target.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            return true;

        var lower = target.ToLowerInvariant();
        if (lower.Contains("server=") || lower.Contains("data source=") || lower.Contains("user id="))
            return true;

        return false;
    }

    private async Task<DatabaseBackupResult> BackupSqliteAsync(string target, string backupDir, CancellationToken ct)
    {
        var result = new DatabaseBackupResult { TargetType = "sqlite" };

        if (!File.Exists(target))
        {
            result.ErrorMessage = $"Fichier SQLite introuvable : {target}";
            return result;
        }

        var ext = Path.GetExtension(target);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupFileName = $"db_{timestamp}{ext}";
        var backupPath = Path.Combine(backupDir, backupFileName);

        File.Copy(target, backupPath, true);

        var fi = new FileInfo(backupPath);
        result.Success = true;
        result.OutputPath = backupPath;
        result.SizeBytes = fi.Length;
        result.CreatedAt = fi.CreationTimeUtc;

        _logger.LogInformation("[DatabaseBackup] SQLite sauvegardé : {Path} ({Size} octets)", backupPath, fi.Length);

        return result;
    }

    private async Task<DatabaseBackupResult> BackupMySqlAsync(string target, string backupDir, CancellationToken ct)
    {
        var result = new DatabaseBackupResult { TargetType = "mysql" };

        if (target.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(target))
            {
                result.ErrorMessage = $"Fichier SQL introuvable : {target}";
                return result;
            }

            var ext = Path.GetExtension(target);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"db_{timestamp}{ext}";
            var backupPath = Path.Combine(backupDir, backupFileName);

            File.Copy(target, backupPath, true);

            var fi = new FileInfo(backupPath);
            result.Success = true;
            result.OutputPath = backupPath;
            result.SizeBytes = fi.Length;
            result.CreatedAt = fi.CreationTimeUtc;

            return result;
        }

        var conn = ParseConnectionString(target);
        if (conn.Host is null || conn.Database is null || conn.User is null)
        {
            result.ErrorMessage = "Impossible d'analyser la chaîne de connexion MySQL. Format attendu : Server=host;Port=3306;Database=name;User Id=u;Password=p";
            return result;
        }

        var timestamp2 = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dumpFile = Path.Combine(backupDir, $"db_{timestamp2}.sql");

        var port = conn.Port ?? 3306;
        var pwdArg = string.IsNullOrEmpty(conn.Password) ? "" : $"--password={conn.Password}";
        var arguments = $"-h \"{conn.Host}\" -P {port} -u \"{conn.User}\" {pwdArg} \"{conn.Database}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "mysqldump",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                result.ErrorMessage = "Impossible de démarrer mysqldump";
                return result;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                result.ErrorMessage = $"mysqldump a échoué (code {process.ExitCode}) : {error}";
                return result;
            }

            await File.WriteAllTextAsync(dumpFile, output, ct);

            var fi = new FileInfo(dumpFile);
            result.Success = true;
            result.OutputPath = dumpFile;
            result.SizeBytes = fi.Length;
            result.CreatedAt = fi.CreationTimeUtc;

            _logger.LogInformation("[DatabaseBackup] MySQL sauvegardé : {Path} ({Size} octets)", dumpFile, fi.Length);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            result.ErrorMessage = "mysqldump introuvable. Installe MySQL/MariaDB ou ajoutez-le au PATH.";
        }

        return result;
    }

    private static MySqlConn ParseConnectionString(string connectionString)
    {
        var conn = new MySqlConn();
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;

            var key = kv[0].Trim().ToLowerInvariant();
            var value = kv[1].Trim();

            switch (key)
            {
                case "server":
                case "host":
                    conn.Host = value;
                    break;
                case "port":
                    if (int.TryParse(value, out var p)) conn.Port = p;
                    break;
                case "database":
                    conn.Database = value;
                    break;
                case "user id":
                case "user":
                    conn.User = value;
                    break;
                case "password":
                    conn.Password = value;
                    break;
            }
        }

        return conn;
    }

    private sealed class MySqlConn
    {
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? Database { get; set; }
        public string? User { get; set; }
        public string? Password { get; set; }
    }
}

public sealed class DatabaseBackupResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public string TargetType { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class DatabaseBackupInfo
{
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string FilePath { get; set; } = "";
}
