using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ProcessTool : ITool
{
    private readonly ILogger<ProcessTool> _logger;

    public string Name => "process";
    public string Description => "Lance et gère des applications et processus sur l'ordinateur. Utilise start_process pour ouvrir/lancer un programme (blender, spotify, chrome, notepad, tout.exe), stop_process pour arrêter, list_processes pour voir ce qui tourne, find_process pour chercher. Quand l'utilisateur dit 'ouvre X' ou 'lance X', utilise start_process avec name=X.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Operation: start_process, stop_process, list_processes, find_process", typeof(string), required: true),
        new ToolParameter("name", "Process name or executable path (for start/stop/find)", typeof(string)),
        new ToolParameter("arguments", "Command line arguments (for start_process)", typeof(string)),
        new ToolParameter("working_directory", "Working directory (for start_process)", typeof(string)),
        new ToolParameter("pid", "Process ID (for stop_process by ID)", typeof(string)),
    };

    public ProcessTool(ILogger<ProcessTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("name", out var name);
        parameters.TryGetValue("arguments", out var args);
        parameters.TryGetValue("working_directory", out var workingDir);
        parameters.TryGetValue("pid", out var pidStr);

        try
        {
            return (action?.ToLowerInvariant()) switch
            {
                "start_process" => await StartProcessAsync(name, args, workingDir, cancellationToken),
                "stop_process" => await StopProcessAsync(name, pidStr, cancellationToken),
                "list_processes" => await ListProcessesAsync(cancellationToken),
                "find_process" => await FindProcessAsync(name, cancellationToken),
                _ => ToolResult.Failed($"Unknown action: {action}. Valid: start_process, stop_process, list_processes, find_process")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProcessTool] Action {Action} failed", action);
            return ToolResult.Failed($"Process error: {ex.Message}");
        }
    }

    private Task<ToolResult> StartProcessAsync(string? name, string? arguments, string? workingDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Failed("Parameter 'name' is required for start_process"));

        // Si le nom ne contient pas de chemin, chercher dans les dossiers d'installation courants
        var resolvedName = ResolveExecutablePath(name);

        var psi = new ProcessStartInfo
        {
            FileName = resolvedName,
            Arguments = arguments ?? "",
            WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };

        var process = Process.Start(psi);
        if (process == null)
            return Task.FromResult(ToolResult.Failed($"Failed to start process: {name}"));

        _logger.LogInformation("[ProcessTool] Started process: {Name} (PID={Pid})", name, process.Id);
        return Task.FromResult(ToolResult.Succeeded($"Process started: {name} (PID: {process.Id})"));
    }

    /// <summary>
    /// Résout le chemin complet d'un exécutable. Si le nom ne contient pas de chemin,
    /// cherche dans les dossiers d'installation courants (Program Files, etc.).
    /// </summary>
    private static string ResolveExecutablePath(string name)
    {
        // Si c'est déjà un chemin complet, l'utiliser tel quel
        if (name.Contains('\\') || name.Contains('/'))
            return name;

        // Si ça a une extension .exe, chercher directement
        var exeName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";

        // 1. Chercher via Where.exe (PATH system)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = exeName,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    var firstLine = output.Split('\n')[0].Trim();
                    if (File.Exists(firstLine))
                        return firstLine;
                }
            }
        }
        catch { /* Where failed, continue with manual search */ }

        // 2. Chercher dans les dossiers d'installation courants
        var searchDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            @"C:\ProgramData",
        };

        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;

            try
            {
                // Chercher récursivement (max 3 niveaux) pour trouver l'exe
                foreach (var found in Directory.EnumerateFiles(dir, exeName, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    IgnoreInaccessible = true
                }))
                {
                    return found;
                }
            }
            catch { /* Skip inaccessible dirs */ }
        }

        // 3. Retourner le nom tel quel (Windows essaiera via ShellExecute)
        return name;
    }

    private async Task<ToolResult> StopProcessAsync(string? name, string? pidStr, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(pidStr) && int.TryParse(pidStr, out var pid))
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync(ct);
                _logger.LogInformation("[ProcessTool] Killed process PID={Pid}", pid);
                return ToolResult.Succeeded($"Process PID {pid} terminated");
            }
            catch (ArgumentException)
            {
                return ToolResult.Failed($"Process with PID {pid} not found");
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Failed("Provide either 'name' or 'pid' parameter");

        var processes = Process.GetProcessesByName(name);
        if (processes.Length == 0)
            return ToolResult.Failed($"No running process found: {name}");

        var killed = 0;
        foreach (var proc in processes)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync(ct);
                killed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProcessTool] Failed to kill {Name} PID={Pid}", name, proc.Id);
            }
        }

        return ToolResult.Succeeded($"Terminated {killed} process(es) named '{name}'");
    }

    private Task<ToolResult> ListProcessesAsync(CancellationToken ct)
    {
        var processes = Process.GetProcesses()
            .OrderByDescending(p => p.WorkingSet64)
            .Take(100)
            .Select(p =>
            {
                try
                {
                    return new
                    {
                        Name = p.ProcessName,
                        Pid = p.Id,
                        MemoryMB = Math.Round(p.WorkingSet64 / 1024.0 / 1024, 1),
                        CpuTime = p.TotalProcessorTime.ToString(@"hh\:mm\:ss"),
                        StartTime = p.TryGetStartTime(out var start) ? start.ToString("yyyy-MM-dd HH:mm") : "N/A",
                    };
                }
                catch { return null; }
            })
            .Where(p => p != null)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Running processes (top {processes.Count} by memory):");
        sb.AppendLine($"{"PID",-8} {"Name",-25} {"Memory",-10} {"CPU Time",-10} {"Started",-18}");
        sb.AppendLine(new string('-', 75));
        foreach (var p in processes)
        {
            sb.AppendLine($"{p!.Pid,-8} {p.Name,-25} {p.MemoryMB + " MB",-10} {p.CpuTime,-10} {p.StartTime,-18}");
        }

        return Task.FromResult(ToolResult.Succeeded(sb.ToString()));
    }

    private Task<ToolResult> FindProcessAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Failed("Parameter 'name' is required for find_process"));

        var sb = new StringBuilder();

        // 1. Chercher les processus en cours
        var processes = Process.GetProcessesByName(name);
        if (processes.Length > 0)
        {
            sb.AppendLine($"Running: {processes.Length} process(es) named '{name}':");
            foreach (var p in processes.OrderBy(p => p.Id))
            {
                try
                {
                    var memMB = Math.Round(p.WorkingSet64 / 1024.0 / 1024, 1);
                    var startTime = p.TryGetStartTime(out var start) ? start.ToString("yyyy-MM-MM HH:mm:ss") : "N/A";
                    sb.AppendLine($"  PID {p.Id}: {memMB} MB, started {startTime}, threads: {p.Threads.Count}");
                }
                catch { sb.AppendLine($"  PID {p.Id}: (access denied)"); }
            }
        }

        // 2. Chercher l'exécutable installé (même s'il n'est pas en cours)
        var exeName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        var resolved = ResolveExecutablePath(name);
        if (resolved != name && File.Exists(resolved))
        {
            sb.AppendLine($"Installed: {resolved}");
        }
        else
        {
            // Chercher dans les dossiers d'installation
            var searchDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            };

            foreach (var dir in searchDirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    continue;
                try
                {
                    foreach (var found in Directory.EnumerateFiles(dir, exeName, new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 3,
                        IgnoreInaccessible = true
                    }))
                    {
                        sb.AppendLine($"Installed: {found}");
                        break;
                    }
                }
                catch { }
            }
        }

        if (sb.Length == 0)
            return Task.FromResult(ToolResult.Succeeded($"Not found: '{name}' (not running and not installed in common directories)"));

        return Task.FromResult(ToolResult.Succeeded(sb.ToString()));
    }
}

internal static class ProcessExtensions
{
    public static bool TryGetStartTime(this Process process, out DateTime startTime)
    {
        try
        {
            startTime = process.StartTime;
            return true;
        }
        catch
        {
            startTime = default;
            return false;
        }
    }
}
