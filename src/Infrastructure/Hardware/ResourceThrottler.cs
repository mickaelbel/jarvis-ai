using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Hardware;

public interface IResourceThrottler
{
    ResourceSnapshot GetCurrentUsage();
    bool ShouldThrottle();
    TimeSpan GetOptimalInterval();
}

public sealed class ResourceThrottler : IResourceThrottler, IDisposable
{
    private readonly ILogger<ResourceThrottler> _logger;
    private readonly Timer _monitorTimer;
    private ResourceSnapshot _current = new();
    private bool _throttling;

    public ResourceThrottler(ILogger<ResourceThrottler> logger)
    {
        _logger = logger;
        _monitorTimer = new Timer(MonitorCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private void MonitorCallback(object? state)
    {
        try
        {
            var process = Process.GetCurrentProcess();
            _current = new ResourceSnapshot
            {
                Timestamp = DateTime.UtcNow,
                CpuPercent = GetCpuUsage(process),
                MemoryMb = process.WorkingSet64 / 1024.0 / 1024.0,
                ThreadCount = process.Threads.Count
            };

            _throttling = _current.CpuPercent > 80 || _current.MemoryMb > 500;
        }
        catch { }
    }

    private static double GetCpuUsage(Process process)
    {
        try
        {
            var cpuTime = process.TotalProcessorTime;
            var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();
            return uptime.TotalMilliseconds > 0
                ? (cpuTime.TotalMilliseconds / (uptime.TotalMilliseconds * Environment.ProcessorCount)) * 100
                : 0;
        }
        catch { return 0; }
    }

    public ResourceSnapshot GetCurrentUsage() => _current;
    public bool ShouldThrottle() => _throttling;

    public TimeSpan GetOptimalInterval()
    {
        if (_current.CpuPercent > 80) return TimeSpan.FromMinutes(30);
        if (_current.CpuPercent > 50) return TimeSpan.FromMinutes(15);
        return TimeSpan.FromMinutes(5);
    }

    public void Dispose() => _monitorTimer?.Dispose();
}

public sealed class ResourceSnapshot
{
    public DateTime Timestamp { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public int ThreadCount { get; set; }
}
