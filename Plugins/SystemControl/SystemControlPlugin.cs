using JarvisAI.Application.Agents;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using System.Diagnostics;

namespace JarvisAI.Plugins.SystemControl;

public sealed class SystemControlPlugin : IPlugin
{
    public PluginMetadata Metadata { get; } = new()
    {
        Id = "JarvisAI.Plugins.SystemControl",
        Name = "System Control",
        Description = "System information and process management tools",
        Author = "JarvisAI",
        Version = "1.0.0",
        PermissionLevel = PluginPermissionLevel.Medium,
        Permissions = new() { "system_info", "process_list" }
    };

    public PluginState State { get; private set; } = PluginState.Discovered;
    private PluginContext? _context;

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        State = PluginState.Initialized;

        context.RegisterTool(new SystemInfoTool());
        context.RegisterTool(new ProcessListTool());

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        State = PluginState.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _context?.UnregisterTools();
        _context?.DisposeSubscriptions();
        State = PluginState.Stopped;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _context?.DisposeSubscriptions();
    }
}

public sealed class SystemInfoTool : ITool
{
    public string Name => "sys_system_info";
    public string Description => "Returns detailed system information: OS, CPU, memory, uptime";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var info = new
        {
            OS = Environment.OSVersion.ToString(),
            MachineName = Environment.MachineName,
            ProcessorCount = Environment.ProcessorCount,
            WorkingSet = $"{Environment.WorkingSet / 1024 / 1024} MB",
            RuntimeVersion = Environment.Version.ToString(),
            Is64Bit = Environment.Is64BitOperatingSystem,
            UserDomain = Environment.UserDomainName,
            UserName = Environment.UserName,
            ProcessId = process.Id,
            Uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).ToString(@"hh\:mm\:ss")
        };

        return Task.FromResult(ToolResult.Succeeded(System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
    }
}

public sealed class ProcessListTool : ITool
{
    public string Name => "sys_process_list";
    public string Description => "Lists running processes. Optionally filter by name with the 'filter' parameter.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("filter", "Filter processes by name (case-insensitive)", typeof(string))
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("filter", out var filter);

        var processes = Process.GetProcesses();
        var query = processes.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query.Where(p =>
            {
                try { return p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        }

        var result = query
            .Take(50)
            .Select(p =>
            {
                try
                {
                    return new
                    {
                        Name = p.ProcessName,
                        Id = p.Id,
                        MemoryMB = $"{p.WorkingSet64 / 1024 / 1024} MB",
                        CpuTime = p.TotalProcessorTime.ToString(@"hh\:mm\:ss")
                    };
                }
                catch
                {
                    return new { Name = p.ProcessName, Id = p.Id, MemoryMB = "N/A", CpuTime = "N/A" };
                }
            })
            .ToList();

        return Task.FromResult(ToolResult.Succeeded(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
    }
}
