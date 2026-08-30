using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Management;

namespace JarvisAI.Infrastructure.Hardware;

public interface IHealthMonitor
{
    Task<HealthReport> CheckHealthAsync(CancellationToken ct = default);
    event EventHandler<HealthAlert>? AlertRaised;
    void StartMonitoring(TimeSpan interval);
    void StopMonitoring();
}

public sealed class HealthMonitor : IHealthMonitor, IDisposable
{
    private readonly ILogger<HealthMonitor> _logger;
    private Timer? _monitorTimer;
    private readonly List<HealthAlert> _recentAlerts = new();
    private const int MaxAlerts = 100;

    public event EventHandler<HealthAlert>? AlertRaised;

    public HealthMonitor(ILogger<HealthMonitor> logger)
    {
        _logger = logger;
    }

    public void StartMonitoring(TimeSpan interval)
    {
        _monitorTimer = new Timer(MonitorCallback, null, TimeSpan.Zero, interval);
        _logger.LogInformation("[HealthMonitor] Started with interval: {Interval}", interval);
    }

    public void StopMonitoring()
    {
        _monitorTimer?.Dispose();
        _monitorTimer = null;
        _logger.LogInformation("[HealthMonitor] Stopped");
    }

    private void MonitorCallback(object? state)
    {
        _ = CheckHealthInternalAsync();
    }

    private async Task CheckHealthInternalAsync()
    {
        try
        {
            var report = await CheckHealthAsync();

            foreach (var alert in report.Alerts)
            {
                lock (_recentAlerts)
                {
                    _recentAlerts.Add(alert);
                    if (_recentAlerts.Count > MaxAlerts)
                        _recentAlerts.RemoveAt(0);
                }

                AlertRaised?.Invoke(this, alert);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HealthMonitor] Check failed");
        }
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken ct = default)
    {
        var report = new HealthReport { CheckedAt = DateTime.UtcNow };

        try
        {
            var process = Process.GetCurrentProcess();
            report.CpuUsage = GetCpuUsage(process);
            report.MemoryUsageMb = process.WorkingSet64 / 1024.0 / 1024.0;
            report.ThreadCount = process.Threads.Count;
            report.HandleCount = process.HandleCount;

            report.DiskUsage = GetDiskUsage();
            report.IsHealthy = report.CpuUsage < 90 && report.MemoryUsageMb < 1024;

            if (!report.IsHealthy)
            {
                if (report.CpuUsage > 90)
                    report.Alerts.Add(new HealthAlert { Severity = AlertSeverity.Warning, Message = $"High CPU: {report.CpuUsage:F1}%" });
                if (report.MemoryUsageMb > 1024)
                    report.Alerts.Add(new HealthAlert { Severity = AlertSeverity.Warning, Message = $"High memory: {report.MemoryUsageMb:F0} MB" });
            }
        }
        catch (Exception ex)
        {
            report.IsHealthy = false;
            report.Alerts.Add(new HealthAlert { Severity = AlertSeverity.Error, Message = $"Health check failed: {ex.Message}" });
        }

        return report;
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

    private DiskUsageInfo GetDiskUsage()
    {
        try
        {
            var drive = new DriveInfo("C");
            return new DiskUsageInfo
            {
                TotalGb = drive.TotalSize / 1024.0 / 1024.0 / 1024.0,
                FreeGb = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0,
                UsagePercent = (1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100
            };
        }
        catch
        {
            return new DiskUsageInfo();
        }
    }

    public void Dispose()
    {
        _monitorTimer?.Dispose();
    }
}

public sealed class HealthReport
{
    public DateTime CheckedAt { get; set; }
    public bool IsHealthy { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsageMb { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public DiskUsageInfo DiskUsage { get; set; } = new();
    public List<HealthAlert> Alerts { get; set; } = new();
}

public sealed class DiskUsageInfo
{
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
    public double UsagePercent { get; set; }
}

public sealed class HealthAlert
{
    public AlertSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum AlertSeverity { Info, Warning, Error, Critical }
