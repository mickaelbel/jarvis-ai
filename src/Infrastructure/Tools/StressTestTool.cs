using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Threading;

namespace JarvisAI.Infrastructure.Tools;

public sealed class StressTestTool : ToolBase
{
    private readonly object _memLock = new();
    private readonly List<StressTestResult> _results = new();

    public override string Name => "stress_test";
    public override string Description => "Outils de test de charge pour simuler CPU, mémoire, disque ou réseau. Actions: cpu (stress CPU), memory (allouer RAM), disk (écrire fichiers), stop (arrêter tests), status (état actuel).";
    public override string Category => "system";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "cpu, memory, disk, stop, status", typeof(string), required: true),
        new ToolParameter("intensity", "Intensité: low, medium, high (défaut: medium)", typeof(string)),
        new ToolParameter("duration_seconds", "Durée en secondes (défaut: 10)", typeof(string)),
        new ToolParameter("target_mb", "MB de RAM à allouer (memory) (défaut: 100)", typeof(string)),
    };

    private readonly List<byte[]> _allocatedMemory = new();
    private volatile CancellationTokenSource? _cts;

    public StressTestTool(ILogger<StressTestTool> logger) : base(logger)
    {
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("intensity", out var intensity);
        parameters.TryGetValue("duration_seconds", out var durationStr);
        parameters.TryGetValue("target_mb", out var mbStr);

        int duration = 10;
        if (!string.IsNullOrEmpty(durationStr) && int.TryParse(durationStr, out var d))
            duration = Math.Min(d, 60);

        int targetMb = 100;
        if (!string.IsNullOrEmpty(mbStr) && int.TryParse(mbStr, out var mb))
            targetMb = Math.Min(mb, 1024);

        return action?.ToLowerInvariant() switch
        {
            "cpu" => await StressCpuAsync(intensity ?? "medium", duration, cancellationToken),
            "memory" => StressMemory(targetMb),
            "disk" => await StressDiskAsync(intensity ?? "medium", duration, cancellationToken),
            "stop" => StopAll(),
            "status" => GetStatus(),
            _ => ToolResult.Failed($"Action inconnue: {action}. Valides: cpu, memory, disk, stop, status")
        };
    }

    private async Task<ToolResult> StressCpuAsync(string intensity, int duration, CancellationToken ct)
    {
        var threads = intensity switch
        {
            "low" => 1,
            "high" => Environment.ProcessorCount,
            _ => Math.Max(1, Environment.ProcessorCount / 2)
        };

        Logger.LogWarning("[StressTest] CPU stress: {Threads} threads for {Duration}s", threads, duration);
        var oldCts = Interlocked.Exchange(ref _cts, CancellationTokenSource.CreateLinkedTokenSource(ct));
        oldCts?.Dispose();

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            while (!_cts.Token.IsCancellationRequested && sw.Elapsed.TotalSeconds < duration)
            {
                Math.Sqrt(123456.789);
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        var result = new StressTestResult { Type = "cpu", Duration = sw.Elapsed, Threads = threads, Timestamp = DateTime.UtcNow };
        lock (_memLock) _results.Add(result);

        return ToolResult.Succeeded($"CPU stress terminé: {threads} threads pendant {sw.Elapsed.TotalSeconds:F1}s");
    }

    private ToolResult StressMemory(int targetMb)
    {
        try
        {
            Logger.LogWarning("[StressTest] Memory allocation: {Target}MB", targetMb);
            var data = new byte[targetMb * 1024 * 1024];
            new Random(42).NextBytes(data);
            lock (_memLock)
            {
                _allocatedMemory.Add(data);
                var result = new StressTestResult { Type = "memory", SizeMb = targetMb, Timestamp = DateTime.UtcNow };
                _results.Add(result);
                var totalMb = _allocatedMemory.Sum(a => a.Length / 1024 / 1024);
                return ToolResult.Succeeded($"Mémoire allouée: {targetMb}MB (total: {totalMb}MB)");
            }
        }
        catch (OutOfMemoryException)
        {
            return ToolResult.Failed("Pas assez de mémoire disponible");
        }
    }

    private async Task<ToolResult> StressDiskAsync(string intensity, int duration, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis_stress_test");
        Directory.CreateDirectory(dir);

        var fileSize = intensity switch
        {
            "low" => 1024 * 1024,
            "high" => 100 * 1024 * 1024,
            _ => 10 * 1024 * 1024
        };

        var sw = Stopwatch.StartNew();
        var filesWritten = 0;

        while (sw.Elapsed.TotalSeconds < duration && !ct.IsCancellationRequested)
        {
            var file = Path.Combine(dir, $"test_{filesWritten}.dat");
            var data = new byte[fileSize];
            await File.WriteAllBytesAsync(file, data, ct);
            filesWritten++;
        }

        sw.Stop();

        try { Directory.Delete(dir, recursive: true); } catch { }

        var result = new StressTestResult { Type = "disk", Duration = sw.Elapsed, FilesWritten = filesWritten, Timestamp = DateTime.UtcNow };
        lock (_memLock) _results.Add(result);

        return ToolResult.Succeeded($"Disk stress terminé: {filesWritten} fichiers ({fileSize / 1024 / 1024}MB chacun) en {sw.Elapsed.TotalSeconds:F1}s");
    }

    private ToolResult StopAll()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        cts?.Dispose();
        lock (_memLock) _allocatedMemory.Clear();
        return ToolResult.Succeeded("Tous les tests arrêtés, mémoire libérée");
    }

    private ToolResult GetStatus()
    {
        var process = Process.GetCurrentProcess();
        var memMb = process.WorkingSet64 / 1024.0 / 1024.0;

        int allocatedMb;
        List<string> recentResults;
        lock (_memLock)
        {
            allocatedMb = _allocatedMemory.Sum(a => a.Length / 1024 / 1024);
            recentResults = _results.TakeLast(5).Select(r =>
                $"  {r.Timestamp:HH:mm:ss} - {r.Type}: {r.Duration?.TotalSeconds:F1}s" +
                (r.Type == "memory" ? $" ({r.SizeMb}MB)" : "") +
                (r.Type == "disk" ? $" ({r.FilesWritten} files)" : "")).ToList();
        }

        return ToolResult.Succeeded($"Status:\nMémoire process: {memMb:F0}MB\nMémoire allouée: {allocatedMb}MB\nTests récents:\n{string.Join("\n", recentResults)}");
    }
}

internal sealed class StressTestResult
{
    public string Type { get; set; } = "";
    public TimeSpan? Duration { get; set; }
    public int Threads { get; set; }
    public int SizeMb { get; set; }
    public int FilesWritten { get; set; }
    public DateTime Timestamp { get; set; }
}
