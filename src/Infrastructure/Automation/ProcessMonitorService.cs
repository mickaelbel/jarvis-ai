using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface IProcessMonitorService
{
    Task<ProcessSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task<List<ProcessInfo>> GetTopCpuProcessesAsync(int count = 10, CancellationToken ct = default);
    Task<List<ProcessInfo>> GetTopMemoryProcessesAsync(int count = 10, CancellationToken ct = default);
    Task<ProcessAlert> MonitorAndAlertAsync(double cpuThreshold = 80, double memoryThreshold = 80, int durationSeconds = 60, CancellationToken ct = default);
    Task<bool> KillProcessAsync(int pid, CancellationToken ct = default);
    Task<bool> KillProcessByNameAsync(string name, CancellationToken ct = default);
    Task<List<ProcessInfo>> SearchProcessesAsync(string query, CancellationToken ct = default);
}

public sealed class ProcessMonitorService : IProcessMonitorService
{
    private readonly ILogger<ProcessMonitorService> _logger;

    public ProcessMonitorService(ILogger<ProcessMonitorService> logger)
    {
        _logger = logger;
    }

    public Task<ProcessSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = new ProcessSnapshot
        {
            Timestamp = DateTime.UtcNow,
            TotalProcesses = Process.GetProcesses().Length,
            TotalCpuUsage = GetTotalCpuUsage(),
            TotalMemoryUsedMb = GetTotalMemoryUsedMb(),
            TotalMemoryFreeMb = GetTotalMemoryFreeMb()
        };

        var processes = Process.GetProcesses()
            .Select(p => new ProcessInfo
            {
                Id = p.Id,
                Name = p.ProcessName,
                CpuPercent = GetProcessCpu(p),
                MemoryMb = GetProcessMemory(p),
                ThreadCount = p.Threads.Count,
                StartTime = SafeGetStartTime(p),
                MainWindowTitle = p.MainWindowTitle
            })
            .Where(p => p.MemoryMb > 0)
            .OrderByDescending(p => p.MemoryMb)
            .Take(50)
            .ToList();

        snapshot.Processes = processes;
        return Task.FromResult(snapshot);
    }

    public async Task<List<ProcessInfo>> GetTopCpuProcessesAsync(int count = 10, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct);
        return snapshot.Processes
            .OrderByDescending(p => p.CpuPercent)
            .Take(count)
            .ToList();
    }

    public async Task<List<ProcessInfo>> GetTopMemoryProcessesAsync(int count = 10, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct);
        return snapshot.Processes
            .OrderByDescending(p => p.MemoryMb)
            .Take(count)
            .ToList();
    }

    public async Task<ProcessAlert> MonitorAndAlertAsync(double cpuThreshold = 80, double memoryThreshold = 80, int durationSeconds = 60, CancellationToken ct = default)
    {
        var alert = new ProcessAlert
        {
            StartedAt = DateTime.UtcNow,
            CpuThreshold = cpuThreshold,
            MemoryThreshold = memoryThreshold
        };

        var startTime = DateTime.UtcNow;
        while (DateTime.UtcNow - startTime < TimeSpan.FromSeconds(durationSeconds) && !ct.IsCancellationRequested)
        {
            var snapshot = await GetSnapshotAsync(ct);

            if (snapshot.TotalCpuUsage > cpuThreshold)
            {
                alert.HighCpuDetected = true;
                alert.HighCpuProcesses = snapshot.Processes
                    .Where(p => p.CpuPercent > 20)
                    .Select(p => p.Name)
                    .Distinct()
                    .ToList();
            }

            if (snapshot.TotalMemoryUsedMb > memoryThreshold * 100) // Convert % to MB (assuming 100GB total)
            {
                alert.HighMemoryDetected = true;
                alert.HighMemoryProcesses = snapshot.Processes
                    .Where(p => p.MemoryMb > 500)
                    .Select(p => p.Name)
                    .Distinct()
                    .ToList();
            }

            await Task.Delay(5000, ct);
        }

        alert.CompletedAt = DateTime.UtcNow;
        alert.Duration = alert.CompletedAt.Value - alert.StartedAt;

        if (alert.HighCpuDetected || alert.HighMemoryDetected)
        {
            _logger.LogWarning("[ProcessMonitor] Alert: CPU={Cpu}, Memory={Mem}",
                alert.HighCpuDetected, alert.HighMemoryDetected);
        }

        return alert;
    }

    public Task<bool> KillProcessAsync(int pid, CancellationToken ct = default)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            process.Kill();
            _logger.LogInformation("[ProcessMonitor] Killed process: {Pid} ({Name})", pid, process.ProcessName);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProcessMonitor] Failed to kill process: {Pid}", pid);
            return Task.FromResult(false);
        }
    }

    public Task<bool> KillProcessByNameAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                process.Kill();
                _logger.LogInformation("[ProcessMonitor] Killed process: {Name} (PID: {Pid})", name, process.Id);
            }
            return Task.FromResult(processes.Length > 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProcessMonitor] Failed to kill process: {Name}", name);
            return Task.FromResult(false);
        }
    }

    public Task<List<ProcessInfo>> SearchProcessesAsync(string query, CancellationToken ct = default)
    {
        var results = Process.GetProcesses()
            .Where(p => p.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ProcessInfo
            {
                Id = p.Id,
                Name = p.ProcessName,
                CpuPercent = GetProcessCpu(p),
                MemoryMb = GetProcessMemory(p),
                ThreadCount = p.Threads.Count,
                StartTime = SafeGetStartTime(p),
                MainWindowTitle = p.MainWindowTitle
            })
            .OrderByDescending(p => p.MemoryMb)
            .ToList();

        return Task.FromResult(results);
    }

    private static double GetTotalCpuUsage()
    {
        try
        {
            using var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            cpuCounter.NextValue();
            Thread.Sleep(100);
            return cpuCounter.NextValue();
        }
        catch
        {
            return 0;
        }
    }

    private static double GetTotalMemoryUsedMb()
    {
        using var memCounter = new PerformanceCounter("Memory", "Available MBytes");
        var availableMb = memCounter.NextValue();
        var totalMb = GetTotalMemoryMb();
        return totalMb - availableMb;
    }

    private static double GetTotalMemoryFreeMb()
    {
        using var memCounter = new PerformanceCounter("Memory", "Available MBytes");
        return memCounter.NextValue();
    }

    private static double GetTotalMemoryMb()
    {
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);
    }

    private static double GetProcessCpu(Process process)
    {
        try
        {
            using var counter = new PerformanceCounter("Process", "% Processor Time", process.ProcessName, true);
            counter.NextValue();
            Thread.Sleep(100);
            return counter.NextValue() / Environment.ProcessorCount;
        }
        catch
        {
            return 0;
        }
    }

    private static double GetProcessMemory(Process process)
    {
        try
        {
            return process.WorkingSet64 / (1024.0 * 1024.0);
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime? SafeGetStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class ProcessSnapshot
{
    public DateTime Timestamp { get; set; }
    public int TotalProcesses { get; set; }
    public double TotalCpuUsage { get; set; }
    public double TotalMemoryUsedMb { get; set; }
    public double TotalMemoryFreeMb { get; set; }
    public List<ProcessInfo> Processes { get; set; } = new();
}

public sealed class ProcessInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public int ThreadCount { get; set; }
    public DateTime? StartTime { get; set; }
    public string MainWindowTitle { get; set; } = "";
}

public sealed class ProcessAlert
{
    public bool HighCpuDetected { get; set; }
    public bool HighMemoryDetected { get; set; }
    public List<string> HighCpuProcesses { get; set; } = new();
    public List<string> HighMemoryProcesses { get; set; } = new();
    public double CpuThreshold { get; set; }
    public double MemoryThreshold { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
}
