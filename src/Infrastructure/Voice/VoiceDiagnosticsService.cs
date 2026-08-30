using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceDiagnosticsService
{
    Task<VoiceDiagnosticsReport> RunFullDiagnosticsAsync(CancellationToken ct = default);
    Task<VoiceDiagnosticsReport> RunQuickCheckAsync(CancellationToken ct = default);
    VoicePerformanceMetrics GetPerformanceMetrics();
    void RecordLatency(string operation, TimeSpan latency);
    IReadOnlyList<VoiceLatencyRecord> GetLatencyHistory();
    Task<string> GenerateDiagnosticReportAsync();
}

public sealed class VoiceDiagnosticsService : IVoiceDiagnosticsService
{
    private readonly ILogger<VoiceDiagnosticsService> _logger;
    private readonly string _storagePath;
    private readonly VoicePerformanceMetrics _metrics = new();
    private readonly List<VoiceLatencyRecord> _latencyHistory = new();
    private readonly Dictionary<string, List<TimeSpan>> _latencies = new();

    public VoiceDiagnosticsService(ILogger<VoiceDiagnosticsService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "voice_diagnostics.json");
        Load();
    }

    public async Task<VoiceDiagnosticsReport> RunFullDiagnosticsAsync(CancellationToken ct = default)
    {
        var report = new VoiceDiagnosticsReport
        {
            StartedAt = DateTime.UtcNow,
            Checks = new List<VoiceDiagnosticCheck>()
        };

        // Check Python
        report.Checks.Add(await CheckPythonAsync(ct));

        // Check STT server
        report.Checks.Add(await CheckServerAsync("STT", 17001, ct));

        // Check Wake Word server
        report.Checks.Add(await CheckServerAsync("WakeWord", 17002, ct));

        // Check TTS server
        report.Checks.Add(await CheckServerAsync("TTS", 17004, ct));

        // Check Ollama
        report.Checks.Add(await CheckServerAsync("Ollama", 11434, ct));

        // Check microphone
        report.Checks.Add(await CheckMicrophoneAsync(ct));

        // Check speakers
        report.Checks.Add(await CheckSpeakersAsync(ct));

        // Check CUDA
        report.Checks.Add(await CheckCudaAsync(ct));

        report.CompletedAt = DateTime.UtcNow;
        report.Duration = report.CompletedAt.Value - report.StartedAt;
        report.OverallStatus = report.Checks.All(c => c.Passed) ? "Healthy" : "Degraded";

        _logger.LogInformation("[VoiceDiagnostics] Completed: {Status} in {Ms}ms",
            report.OverallStatus, report.Duration?.TotalMilliseconds ?? 0);

        return report;
    }

    public async Task<VoiceDiagnosticsReport> RunQuickCheckAsync(CancellationToken ct = default)
    {
        var report = new VoiceDiagnosticsReport
        {
            StartedAt = DateTime.UtcNow,
            Checks = new List<VoiceDiagnosticCheck>()
        };

        report.Checks.Add(await CheckServerAsync("STT", 17001, ct));
        report.Checks.Add(await CheckServerAsync("TTS", 17004, ct));

        report.CompletedAt = DateTime.UtcNow;
        report.Duration = report.CompletedAt.Value - report.StartedAt;
        report.OverallStatus = report.Checks.All(c => c.Passed) ? "Healthy" : "Degraded";

        return report;
    }

    public VoicePerformanceMetrics GetPerformanceMetrics() => _metrics;

    public void RecordLatency(string operation, TimeSpan latency)
    {
        if (!_latencies.ContainsKey(operation))
            _latencies[operation] = new List<TimeSpan>();

        _latencies[operation].Add(latency);
        if (_latencies[operation].Count > 100)
            _latencies[operation].RemoveAt(0);

        _latencyHistory.Add(new VoiceLatencyRecord
        {
            Operation = operation,
            Latency = latency,
            Timestamp = DateTime.UtcNow
        });

        if (_latencyHistory.Count > 1000)
            _latencyHistory.RemoveAt(0);

        // Update metrics
        _metrics.TotalOperations++;
        _metrics.AverageLatencyMs = _latencies.Values.SelectMany(l => l).Average(l => l.TotalMilliseconds);

        Save();
    }

    public IReadOnlyList<VoiceLatencyRecord> GetLatencyHistory()
        => _latencyHistory.AsReadOnly();

    public async Task<string> GenerateDiagnosticReportAsync()
    {
        var report = await RunFullDiagnosticsAsync();
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Rapport Diagnostics Vocal");
        sb.AppendLine($"Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine($"Statut: {report.OverallStatus}");
        sb.AppendLine($"Durée: {report.Duration?.TotalMilliseconds ?? 0:F0}ms");
        sb.AppendLine();
        sb.AppendLine("## Vérifications");

        foreach (var check in report.Checks)
        {
            var status = check.Passed ? "✓" : "✗";
            sb.AppendLine($"- {status} {check.Name}: {check.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("## Métriques");
        sb.AppendLine($"- Opérations totales: {_metrics.TotalOperations}");
        sb.AppendLine($"- Latence moyenne: {_metrics.AverageLatencyMs:F1}ms");

        return sb.ToString();
    }

    private async Task<VoiceDiagnosticCheck> CheckPythonAsync(CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "python.exe",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            var output = await process!.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return new VoiceDiagnosticCheck
            {
                Name = "Python",
                Passed = output.Contains("Python 3"),
                Message = output.Trim()
            };
        }
        catch
        {
            return new VoiceDiagnosticCheck
            {
                Name = "Python",
                Passed = false,
                Message = "Python non trouvé"
            };
        }
    }

    private async Task<VoiceDiagnosticCheck> CheckServerAsync(string name, int port, CancellationToken ct)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            var response = await client.GetAsync($"http://127.0.0.1:{port}/health", ct);

            return new VoiceDiagnosticCheck
            {
                Name = name,
                Passed = response.IsSuccessStatusCode,
                Message = response.IsSuccessStatusCode ? "En cours d'exécution" : "Erreurs"
            };
        }
        catch
        {
            return new VoiceDiagnosticCheck
            {
                Name = name,
                Passed = false,
                Message = "Arrêté"
            };
        }
    }

    private async Task<VoiceDiagnosticCheck> CheckMicrophoneAsync(CancellationToken ct)
    {
        try
        {
            var waveIn = new NAudio.Wave.WaveInEvent();
            waveIn.Dispose();
            return new VoiceDiagnosticCheck
            {
                Name = "Microphone",
                Passed = true,
                Message = "Disponible"
            };
        }
        catch
        {
            return new VoiceDiagnosticCheck
            {
                Name = "Microphone",
                Passed = false,
                Message = "Non disponible"
            };
        }
    }

    private async Task<VoiceDiagnosticCheck> CheckSpeakersAsync(CancellationToken ct)
    {
        try
        {
            var waveOut = new NAudio.Wave.WaveOutEvent();
            waveOut.Dispose();
            return new VoiceDiagnosticCheck
            {
                Name = "Haut-parleurs",
                Passed = true,
                Message = "Disponibles"
            };
        }
        catch
        {
            return new VoiceDiagnosticCheck
            {
                Name = "Haut-parleurs",
                Passed = false,
                Message = "Non disponibles"
            };
        }
    }

    private async Task<VoiceDiagnosticCheck> CheckCudaAsync(CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            var output = await process!.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return new VoiceDiagnosticCheck
            {
                Name = "CUDA/GPU",
                Passed = !string.IsNullOrEmpty(output),
                Message = output.Trim()
            };
        }
        catch
        {
            return new VoiceDiagnosticCheck
            {
                Name = "CUDA/GPU",
                Passed = false,
                Message = "Non disponible"
            };
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<DiagnosticsData>(json);
                if (data is not null)
                {
                    _latencyHistory.AddRange(data.LatencyHistory);
                    _metrics.TotalOperations = data.Metrics?.TotalOperations ?? 0;
                    _metrics.AverageLatencyMs = data.Metrics?.AverageLatencyMs ?? 0;
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new DiagnosticsData
            {
                LatencyHistory = _latencyHistory.TakeLast(500).ToList(),
                Metrics = _metrics
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class VoiceDiagnosticsReport
{
    public string OverallStatus { get; set; } = "";
    public List<VoiceDiagnosticCheck> Checks { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
}

public sealed class VoiceDiagnosticCheck
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
}

public sealed class VoicePerformanceMetrics
{
    public long TotalOperations { get; set; }
    public double AverageLatencyMs { get; set; }
    public double MinLatencyMs { get; set; }
    public double MaxLatencyMs { get; set; }
}

public sealed class VoiceLatencyRecord
{
    public string Operation { get; set; } = "";
    public TimeSpan Latency { get; set; }
    public DateTime Timestamp { get; set; }
}

internal class DiagnosticsData
{
    public List<VoiceLatencyRecord> LatencyHistory { get; set; } = new();
    public VoicePerformanceMetrics? Metrics { get; set; }
}
