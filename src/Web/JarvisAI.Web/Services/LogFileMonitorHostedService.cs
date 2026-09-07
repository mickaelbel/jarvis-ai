using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public sealed class LogMonitorConfig
{
    public int IntervalSeconds { get; set; } = 30;
    public string LogPath { get; set; } = "";
    public List<string> Keywords { get; set; } = new() { "error", "exception", "fatal", "critical", "stack trace" };
}

public static class LogMonitorConfigStore
{
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "config", "log_monitor.json");

    public static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "config", "log_monitor_state.json");

    public static LogMonitorConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<LogMonitorConfig>(File.ReadAllText(ConfigPath));
                if (loaded is not null)
                {
                    if (loaded.IntervalSeconds <= 0) loaded.IntervalSeconds = 30;
                    loaded.Keywords ??= new List<string> { "error", "exception", "fatal", "critical", "stack trace" };
                    return loaded;
                }
            }
        }
        catch
        {
        }
        return new LogMonitorConfig();
    }

    public static void Save(LogMonitorConfig config)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class LogFileMonitorHostedService : BackgroundService
{
    private readonly ILogAggregationService _logs;
    private readonly INotificationRoutingService _notifier;
    private readonly ILogger<LogFileMonitorHostedService> _logger;
    private Dictionary<string, long> _offsets = new();

    public LogFileMonitorHostedService(
        ILogAggregationService logs,
        INotificationRoutingService notifier,
        ILogger<LogFileMonitorHostedService> logger)
    {
        _logs = logs;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadOffsets();
        var noPathWarned = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = LogMonitorConfigStore.Load();
                var interval = config.IntervalSeconds > 0 ? config.IntervalSeconds : 30;

                if (string.IsNullOrWhiteSpace(config.LogPath))
                {
                    if (!noPathWarned)
                    {
                        _logger.LogInformation("[LogMonitor] Aucun chemin de log configuré (voir log_monitor.json)");
                        noPathWarned = true;
                    }
                }
                else
                {
                    noPathWarned = false;
                    var keywords = config.Keywords is { Count: > 0 }
                        ? config.Keywords
                        : new List<string> { "error", "exception", "fatal", "critical", "stack trace" };
                    Scan(config.LogPath, keywords);
                }

                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LogMonitor] Erreur de boucle");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        SaveOffsets();
    }

    private void Scan(string path, List<string> keywords)
    {
        var files = CollectLogFiles(path);
        var alerts = new List<(string File, string Line, string Keyword)>();

        foreach (var file in files)
        {
            try
            {
                ScanFile(file, keywords, alerts);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[LogMonitor] Erreur de lecture: {File}", file);
            }
        }

        SaveOffsets();

        if (alerts.Count > 0)
            FlushAlerts(alerts);
    }

    private void ScanFile(string file, List<string> keywords, List<(string File, string Line, string Keyword)> alerts)
    {
        var fileInfo = new FileInfo(file);
        if (!fileInfo.Exists) return;

        var fileLength = fileInfo.Length;
        long offset = _offsets.TryGetValue(file, out var stored) ? stored : 0;
        if (offset > fileLength)
            offset = 0;

        if (!_offsets.ContainsKey(file))
        {
            _offsets[file] = fileLength;
            return;
        }

        if (offset >= fileLength)
            return;

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        fs.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[81920];
        var lineBytes = new List<byte>(256);
        long chunkOffset = offset;
        long nextOffset = offset;

        int bytesRead;
        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                var b = buffer[i];
                if (b == (byte)'\n')
                {
                    var end = lineBytes.Count;
                    if (end > 0 && lineBytes[end - 1] == (byte)'\r')
                        end--;
                    if (end > 0)
                    {
                        var line = Encoding.UTF8.GetString(lineBytes.GetRange(0, end).ToArray());
                        CheckKeywords(line, file, keywords, alerts);
                    }
                    lineBytes.Clear();
                    nextOffset = chunkOffset + i + 1;
                }
                else
                {
                    if (lineBytes.Count < 65536)
                        lineBytes.Add(b);
                }
            }
            chunkOffset += bytesRead;
        }

        _offsets[file] = nextOffset;
    }

    private void CheckKeywords(string line, string file, List<string> keywords, List<(string File, string Line, string Keyword)> alerts)
    {
        foreach (var keyword in keywords)
        {
            if (line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                alerts.Add((file, line.Trim(), keyword));
                break;
            }
        }
    }

    private void FlushAlerts(List<(string File, string Line, string Keyword)> alerts)
    {
        var sample = alerts.Take(10).ToList();
        foreach (var (file, line, keyword) in sample)
        {
            var message = $"Fichier: {file} — ligne: {line}";
            var props = new Dictionary<string, string>
            {
                ["file"] = file,
                ["keyword"] = keyword
            };
            _logs.AddLog("LogMonitor", LogLevel.Error, $"Erreur critique détectée ({keyword}): {message}", props);
            _notifier.RouteNotification("log_critical", "Erreur critique détectée", message, props);
        }

        _logger.LogWarning("[LogMonitor] {Count} ligne(s) critique(s) détectée(s) — ligne exemplaire: {File} — {Line}",
            alerts.Count, sample[0].File, sample[0].Line);
    }

    private static List<string> CollectLogFiles(string path)
    {
        if (File.Exists(path))
            return new List<string> { path };
        if (Directory.Exists(path))
        {
            return Directory.GetFiles(path, "*.log", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return new List<string>();
    }

    private void LoadOffsets()
    {
        try
        {
            if (File.Exists(LogMonitorConfigStore.StatePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(LogMonitorConfigStore.StatePath));
                if (loaded is not null)
                    _offsets = loaded;
            }
        }
        catch
        {
        }
    }

    private void SaveOffsets()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogMonitorConfigStore.StatePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(LogMonitorConfigStore.StatePath, JsonSerializer.Serialize(_offsets, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LogMonitor] Échec de sauvegarde des offsets");
        }
    }
}