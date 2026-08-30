using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IScreenRecordingService
{
    ScreenRecording StartRecording(string? outputPath = null, ScreenRecordingOptions? options = null);
    ScreenRecording StopRecording(string recordingId);
    IReadOnlyList<ScreenRecording> GetRecordings();
    ScreenRecording? GetRecording(string recordingId);
    void DeleteRecording(string recordingId);
    string GenerateCommentary(ScreenRecording recording);
    void ExportToFormat(string recordingId, string format);
}

public sealed class ScreenRecordingService : IScreenRecordingService
{
    private readonly ILogger<ScreenRecordingService> _logger;
    private readonly string _storagePath;
    private readonly string _recordingsPath;
    private readonly List<ScreenRecording> _recordings = new();
    private readonly Dictionary<string, Process> _activeRecorders = new();

    public ScreenRecordingService(ILogger<ScreenRecordingService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "screen_recordings.json");
        _recordingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "recordings");
        Directory.CreateDirectory(_recordingsPath);
        Load();
    }

    public ScreenRecording StartRecording(string? outputPath = null, ScreenRecordingOptions? options = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var output = outputPath ?? Path.Combine(_recordingsPath, $"recording_{id}.mp4");

        var recording = new ScreenRecording
        {
            Id = id,
            OutputPath = output,
            Status = "recording",
            Options = options ?? new ScreenRecordingOptions(),
            StartedAt = DateTime.UtcNow
        };

        _recordings.Add(recording);

        // Start ffmpeg screen capture
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = BuildFfmpegArgs(output, options),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };

        try
        {
            var process = Process.Start(startInfo);
            if (process is not null)
            {
                _activeRecorders[id] = process;
                recording.ProcessId = process.Id;
            }
        }
        catch (Exception ex)
        {
            recording.Status = "error";
            recording.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[ScreenRecording] Failed to start recording");
        }

        Save();
        _logger.LogInformation("[ScreenRecording] Started: {Id}", id);
        return recording;
    }

    public ScreenRecording StopRecording(string recordingId)
    {
        var recording = _recordings.FirstOrDefault(r => r.Id == recordingId);
        if (recording is null)
            throw new ArgumentException("Recording non trouvé");

        if (_activeRecorders.TryGetValue(recordingId, out var process))
        {
            try
            {
                process.StandardInput.WriteLine("q");
                process.WaitForExit(5000);
                if (!process.HasExited)
                    process.Kill();
            }
            catch { }

            _activeRecorders.Remove(recordingId);
        }

        recording.Status = "completed";
        recording.CompletedAt = DateTime.UtcNow;
        recording.Duration = recording.CompletedAt.Value - recording.StartedAt;

        if (File.Exists(recording.OutputPath))
            recording.FileSizeBytes = new FileInfo(recording.OutputPath).Length;

        Save();
        _logger.LogInformation("[ScreenRecording] Stopped: {Id}, Duration: {Duration}",
            recordingId, recording.Duration);
        return recording;
    }

    public IReadOnlyList<ScreenRecording> GetRecordings()
        => _recordings.OrderByDescending(r => r.StartedAt).ToList();

    public ScreenRecording? GetRecording(string recordingId)
        => _recordings.FirstOrDefault(r => r.Id == recordingId);

    public void DeleteRecording(string recordingId)
    {
        var recording = _recordings.FirstOrDefault(r => r.Id == recordingId);
        if (recording is not null)
        {
            if (File.Exists(recording.OutputPath))
                File.Delete(recording.OutputPath);

            _recordings.Remove(recording);
            Save();
        }
    }

    public string GenerateCommentary(ScreenRecording recording)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Commentaire: {recording.Id}");
        sb.AppendLine($"Date: {recording.StartedAt:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Durée: {recording.Duration}");
        sb.AppendLine();
        sb.AppendLine("## Étapes enregistrées");
        sb.AppendLine("1. Démarrage de l'enregistrement");
        sb.AppendLine("2. Actions sur l'écran");
        sb.AppendLine("3. Fin de l'enregistrement");

        return sb.ToString();
    }

    public void ExportToFormat(string recordingId, string format)
    {
        var recording = _recordings.FirstOrDefault(r => r.Id == recordingId);
        if (recording is null) return;

        var outputPath = Path.ChangeExtension(recording.OutputPath, format);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-i \"{recording.OutputPath}\" \"{outputPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);
        process?.WaitForExit(30000);
    }

    private string BuildFfmpegArgs(string output, ScreenRecordingOptions? options)
    {
        var sb = new StringBuilder();
        sb.Append("-f gdigrab -framerate 30 -i desktop");

        if (options?.Width > 0 && options?.Height > 0)
            sb.Append($" -vf scale={options.Width}:{options.Height}");

        sb.Append($" -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{output}\"");
        return sb.ToString();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<ScreenRecording>>(json);
                if (loaded is not null) _recordings.AddRange(loaded);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_recordings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class ScreenRecording
{
    public string Id { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Status { get; set; } = "";
    public ScreenRecordingOptions Options { get; set; } = new();
    public int? ProcessId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
    public long FileSizeBytes { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ScreenRecordingOptions
{
    public int Fps { get; set; } = 30;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool RecordAudio { get; set; }
    public string Codec { get; set; } = "h264";
}
