using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IInfrastructureHealthService
{
    HealthDashboard GetDashboard();
    IReadOnlyList<ServiceHealth> GetServicesHealth();
    SystemHealth GetSystemHealth();
    IReadOnlyList<PortStatus> GetOpenPorts();
    IReadOnlyList<DiskHealth> GetDiskHealth();
    NetworkHealth GetNetworkHealth();
}

public sealed class InfrastructureHealthService : IInfrastructureHealthService
{
    private readonly ILogger<InfrastructureHealthService> _logger;
    private readonly string _storagePath;
    private readonly List<HealthCheck> _checks = new();

    public InfrastructureHealthService(ILogger<InfrastructureHealthService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "health_history.json");
    }

    public HealthDashboard GetDashboard()
    {
        return new HealthDashboard
        {
            System = GetSystemHealth(),
            Services = GetServicesHealth().ToList(),
            OpenPorts = GetOpenPorts().ToList(),
            Disks = GetDiskHealth().ToList(),
            Network = GetNetworkHealth(),
            GeneratedAt = DateTime.UtcNow
        };
    }

    public IReadOnlyList<ServiceHealth> GetServicesHealth()
    {
        var services = new List<ServiceHealth>();

        // Check Ollama
        services.Add(CheckService("Ollama", "localhost", 11434));
        services.Add(CheckService("Jarvis Web", "localhost", 51844));
        services.Add(CheckService("Edge TTS", "localhost", 17004));

        return services;
    }

    public SystemHealth GetSystemHealth()
    {
        var process = Process.GetCurrentProcess();
        var gc = GC.GetTotalMemory(false);

        return new SystemHealth
        {
            CpuCores = Environment.ProcessorCount,
            OsVersion = Environment.OSVersion.ToString(),
            MemoryUsedMB = gc / 1024 / 1024,
            MemoryTotalMB = Environment.WorkingSet / 1024 / 1024,
            GcGen0 = GC.CollectionCount(0),
            GcGen1 = GC.CollectionCount(1),
            GcGen2 = GC.CollectionCount(2),
            ThreadCount = process.Threads.Count,
            Uptime = DateTime.UtcNow - process.StartTime,
            ProcessorUsage = GetProcessorUsage()
        };
    }

    public IReadOnlyList<PortStatus> GetOpenPorts()
    {
        var ports = new List<PortStatus>
        {
            CheckPort("Ollama", 11434),
            CheckPort("Jarvis Web", 51844),
            CheckPort("Edge TTS", 17004),
            CheckPort("HTTP", 80),
            CheckPort("HTTPS", 443)
        };

        return ports;
    }

    public IReadOnlyList<DiskHealth> GetDiskHealth()
    {
        var drives = new List<DiskHealth>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady)
            {
                drives.Add(new DiskHealth
                {
                    Name = drive.Name,
                    Label = drive.VolumeLabel,
                    TotalGB = drive.TotalSize / 1024 / 1024 / 1024,
                    FreeGB = drive.AvailableFreeSpace / 1024 / 1024 / 1024,
                    UsedGB = (drive.TotalSize - drive.AvailableFreeSpace) / 1024 / 1024 / 1024,
                    UsagePercentage = (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100,
                    FileSystem = drive.DriveFormat
                });
            }
        }

        return drives;
    }

    public NetworkHealth GetNetworkHealth()
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send("8.8.8.8", 5000);

            return new NetworkHealth
            {
                IsConnected = reply.Status == IPStatus.Success,
                LatencyMs = reply.RoundtripTime,
                TargetHost = "8.8.8.8"
            };
        }
        catch
        {
            return new NetworkHealth
            {
                IsConnected = false,
                LatencyMs = -1
            };
        }
    }

    private ServiceHealth CheckService(string name, string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect(host, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));

            return new ServiceHealth
            {
                Name = name,
                Host = host,
                Port = port,
                IsRunning = success,
                Status = success ? "Running" : "Stopped",
                CheckedAt = DateTime.UtcNow
            };
        }
        catch
        {
            return new ServiceHealth
            {
                Name = name,
                Host = host,
                Port = port,
                IsRunning = false,
                Status = "Unreachable",
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    private PortStatus CheckPort(string name, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));

            return new PortStatus
            {
                Port = port,
                Name = name,
                IsOpen = success,
                Process = success ? "Unknown" : ""
            };
        }
        catch
        {
            return new PortStatus
            {
                Port = port,
                Name = name,
                IsOpen = false
            };
        }
    }

    private double GetProcessorUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime.TotalMilliseconds / (DateTime.UtcNow - process.StartTime).TotalMilliseconds * 100;
        }
        catch
        {
            return 0;
        }
    }
}

public sealed class HealthDashboard
{
    public SystemHealth System { get; set; } = new();
    public List<ServiceHealth> Services { get; set; } = new();
    public List<PortStatus> OpenPorts { get; set; } = new();
    public List<DiskHealth> Disks { get; set; } = new();
    public NetworkHealth Network { get; set; } = new();
    public DateTime GeneratedAt { get; set; }
}

public sealed class SystemHealth
{
    public int CpuCores { get; set; }
    public string OsVersion { get; set; } = "";
    public long MemoryUsedMB { get; set; }
    public long MemoryTotalMB { get; set; }
    public int GcGen0 { get; set; }
    public int GcGen1 { get; set; }
    public int GcGen2 { get; set; }
    public int ThreadCount { get; set; }
    public TimeSpan Uptime { get; set; }
    public double ProcessorUsage { get; set; }
}

public sealed class ServiceHealth
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool IsRunning { get; set; }
    public string Status { get; set; } = "";
    public DateTime CheckedAt { get; set; }
}

public sealed class PortStatus
{
    public int Port { get; set; }
    public string Name { get; set; } = "";
    public bool IsOpen { get; set; }
    public string Process { get; set; } = "";
}

public sealed class DiskHealth
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public long TotalGB { get; set; }
    public long FreeGB { get; set; }
    public long UsedGB { get; set; }
    public double UsagePercentage { get; set; }
    public string FileSystem { get; set; } = "";
}

public sealed class NetworkHealth
{
    public bool IsConnected { get; set; }
    public long LatencyMs { get; set; }
    public string TargetHost { get; set; } = "";
}

internal class HealthCheck
{
    public string Name { get; set; } = "";
    public bool IsHealthy { get; set; }
    public DateTime CheckedAt { get; set; }
}
