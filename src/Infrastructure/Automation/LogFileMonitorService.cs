using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface ILogFileMonitorService
{
    Task<LogMonitorResult> ScanAsync(string logPath, string[]? keywords = null, string? stateFilePath = null, CancellationToken ct = default);
    IReadOnlyList<LogAlert> GetRecentAlerts();
}

public sealed class LogFileMonitorService : ILogFileMonitorService
{
    private readonly Microsoft.Extensions.Logging.ILogger<LogFileMonitorService> _logger;
    private readonly List<LogAlert> _alerts = new();
    private const int MaxAlerts = 500;

    private static readonly string[] DefaultKeywords = new[]
    {
        "error", "exception", "fatal", "critical", "unable", "stack trace"
    };

    public LogFileMonitorService(Microsoft.Extensions.Logging.ILogger<LogFileMonitorService> logger)
    {
        _logger = logger;
    }

    public async Task<LogMonitorResult> ScanAsync(string logPath, string[]? keywords = null, string? stateFilePath = null, CancellationToken ct = default)
    {
        var result = new LogMonitorResult();
        var kw = keywords ?? DefaultKeywords;

        try
        {
            var files = GatherFiles(logPath);
            result.FilesScanned = files.Count;

            var state = await LoadStateAsync(stateFilePath);

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists) continue;

                    long previousOffset = state.TryGetValue(file, out var stored) ? stored : 0;
                    long fileSize = fi.Length;

                    if (fileSize < previousOffset)
                        previousOffset = 0;

                    int bytesToRead = (int)(fileSize - previousOffset);
                    if (bytesToRead <= 0) continue;

                    result.BytesScanned += bytesToRead;

                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
                    fs.Position = previousOffset;

                    using var reader = new StreamReader(fs, Encoding.UTF8, true, 4096, leaveOpen: true);
                    string content = await reader.ReadToEndAsync(ct);

                    state[file] = fileSize;

                    var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int lineCount = CountLinesUpTo(file, previousOffset);

                    foreach (var line in lines)
                    {
                        lineCount++;
                        foreach (var keyword in kw)
                        {
                            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                var alert = new LogAlert
                                {
                                    FilePath = file,
                                    Line = line.Trim(),
                                    LineNumber = lineCount,
                                    At = DateTime.UtcNow,
                                    MatchedKeyword = keyword
                                };
                                result.NewAlerts.Add(alert);

                                lock (_alerts)
                                {
                                    _alerts.Add(alert);
                                    if (_alerts.Count > MaxAlerts)
                                        _alerts.RemoveAt(0);
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LogMonitor] Erreur lecture fichier : {File}", file);
                }
            }

            await SaveStateAsync(stateFilePath, state);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[LogMonitor] Erreur scan");
        }

        return result;
    }

    public IReadOnlyList<LogAlert> GetRecentAlerts()
    {
        lock (_alerts)
        {
            return _alerts.ToList().AsReadOnly();
        }
    }

    private static List<string> GatherFiles(string logPath)
    {
        var files = new List<string>();

        if (File.Exists(logPath))
        {
            files.Add(logPath);
        }
        else if (Directory.Exists(logPath))
        {
            files.AddRange(Directory.GetFiles(logPath, "*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .Take(5));
        }

        return files;
    }

    private static async Task<Dictionary<string, long>> LoadStateAsync(string? stateFilePath)
    {
        if (string.IsNullOrEmpty(stateFilePath) || !File.Exists(stateFilePath))
            return new Dictionary<string, long>();

        try
        {
            var json = await File.ReadAllTextAsync(stateFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? new Dictionary<string, long>();
        }
        catch
        {
            return new Dictionary<string, long>();
        }
    }

    private static async Task SaveStateAsync(string? stateFilePath, Dictionary<string, long> state)
    {
        if (string.IsNullOrEmpty(stateFilePath)) return;

        try
        {
            var dir = Path.GetDirectoryName(stateFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(stateFilePath, json);
        }
        catch { }
    }

    private static int CountLinesUpTo(string filePath, long byteOffset)
    {
        if (byteOffset <= 0) return 0;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long toRead = Math.Min(byteOffset, fs.Length);
            var buffer = new byte[toRead];
            int read = fs.Read(buffer, 0, (int)toRead);
            int lines = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n') lines++;
            }
            return lines;
        }
        catch
        {
            return 0;
        }
    }
}

public sealed class LogAlert
{
    public string FilePath { get; set; } = "";
    public string Line { get; set; } = "";
    public int LineNumber { get; set; }
    public DateTime At { get; set; }
    public string? MatchedKeyword { get; set; }
}

public sealed class LogMonitorResult
{
    public bool Success { get; set; }
    public List<LogAlert> NewAlerts { get; set; } = new();
    public int BytesScanned { get; set; }
    public int FilesScanned { get; set; }
    public string? ErrorMessage { get; set; }
}
