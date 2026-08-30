using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IPerformanceBenchmarkService
{
    BenchmarkResult RunBenchmark(string name, Func<Task> action, int iterations = 10);
    IReadOnlyList<BenchmarkResult> GetHistory();
    ComparisonResult CompareBenchmarks(string benchmarkId1, string benchmarkId2);
    string GenerateReport(BenchmarkResult result);
}

public sealed class PerformanceBenchmarkService : IPerformanceBenchmarkService
{
    private readonly ILogger<PerformanceBenchmarkService> _logger;
    private readonly string _storagePath;
    private readonly List<BenchmarkResult> _results = new();

    public PerformanceBenchmarkService(ILogger<PerformanceBenchmarkService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "benchmarks.json");
        Load();
    }

    public BenchmarkResult RunBenchmark(string name, Func<Task> action, int iterations = 10)
    {
        var times = new List<double>();
        var memoryBefore = GC.GetTotalMemory(true);

        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            action().Wait();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        var memoryAfter = GC.GetTotalMemory(false);

        var result = new BenchmarkResult
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Iterations = iterations,
            Times = times,
            AverageMs = times.Average(),
            MinMs = times.Min(),
            MaxMs = times.Max(),
            MedianMs = GetMedian(times),
            P95Ms = GetPercentile(times, 95),
            StandardDeviation = CalculateStdDev(times),
            MemoryDeltaBytes = memoryAfter - memoryBefore,
            RanAt = DateTime.UtcNow
        };

        _results.Add(result);
        Save();

        _logger.LogInformation("[Benchmark] {Name}: avg {Avg:F2}ms, min {Min:F2}ms, max {Max:F2}ms",
            name, result.AverageMs, result.MinMs, result.MaxMs);

        return result;
    }

    public IReadOnlyList<BenchmarkResult> GetHistory()
        => _results.OrderByDescending(r => r.RanAt).ToList();

    public ComparisonResult CompareBenchmarks(string benchmarkId1, string benchmarkId2)
    {
        var b1 = _results.FirstOrDefault(r => r.Id == benchmarkId1);
        var b2 = _results.FirstOrDefault(r => r.Id == benchmarkId2);

        if (b1 is null || b2 is null)
            return new ComparisonResult { Error = "Benchmark non trouvé" };

        var speedup = b1.AverageMs / b2.AverageMs;

        return new ComparisonResult
        {
            Benchmark1 = b1,
            Benchmark2 = b2,
            SpeedupFactor = speedup,
            IsFaster = b2.AverageMs < b1.AverageMs,
            PercentageImprovement = ((b1.AverageMs - b2.AverageMs) / b1.AverageMs) * 100,
            ComparedAt = DateTime.UtcNow
        };
    }

    public string GenerateReport(BenchmarkResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Rapport Benchmark: {result.Name}");
        sb.AppendLine($"Date: {result.RanAt:dd/MM/yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("## Résultats");
        sb.AppendLine($"- Itérations: {result.Iterations}");
        sb.AppendLine($"- Moyenne: {result.AverageMs:F2} ms");
        sb.AppendLine($"- Médiane: {result.MedianMs:F2} ms");
        sb.AppendLine($"- Min: {result.MinMs:F2} ms");
        sb.AppendLine($"- Max: {result.MaxMs:F2} ms");
        sb.AppendLine($"- P95: {result.P95Ms:F2} ms");
        sb.AppendLine($"- Écart-type: {result.StandardDeviation:F2} ms");
        sb.AppendLine($"- Mémoire: {result.MemoryDeltaBytes / 1024:N1} KB");

        return sb.ToString();
    }

    private double GetMedian(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    private double GetPercentile(List<double> values, int percentile)
    {
        var sorted = values.OrderBy(x => x).ToList();
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, index)];
    }

    private double CalculateStdDev(List<double> values)
    {
        double avg = values.Average();
        double sumSquares = values.Sum(v => Math.Pow(v - avg, 2));
        return Math.Sqrt(sumSquares / values.Count);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<BenchmarkResult>>(json);
                if (loaded is not null) _results.AddRange(loaded);
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

            // Keep only last 100 results
            if (_results.Count > 100)
                _results.RemoveRange(0, _results.Count - 100);

            var json = JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class BenchmarkResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Iterations { get; set; }
    public List<double> Times { get; set; } = new();
    public double AverageMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public double MedianMs { get; set; }
    public double P95Ms { get; set; }
    public double StandardDeviation { get; set; }
    public long MemoryDeltaBytes { get; set; }
    public DateTime RanAt { get; set; }
}

public sealed class ComparisonResult
{
    public BenchmarkResult? Benchmark1 { get; set; }
    public BenchmarkResult? Benchmark2 { get; set; }
    public double SpeedupFactor { get; set; }
    public bool IsFaster { get; set; }
    public double PercentageImprovement { get; set; }
    public DateTime ComparedAt { get; set; }
    public string? Error { get; set; }
}
