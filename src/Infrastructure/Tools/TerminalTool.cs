using JarvisAI.Application.Agents;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class TerminalTool : ITool
{
    private readonly ILogger<TerminalTool> _logger;
    private readonly ISecurityManager? _security;

    public string Name => "terminal";
    public string Description => "Execute commands on the user's Windows computer. Use execute_command for CMD commands like running scripts, git, npm, dotnet, or any terminal operation. Use execute_powershell for PowerShell commands. For launching desktop applications, prefer the process tool instead.";
    public string Category => "terminal";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Operation: execute_command, execute_powershell", typeof(string), required: true),
        new ToolParameter("command", "The command to execute", typeof(string), required: true),
        new ToolParameter("working_directory", "Working directory for the command (optional)", typeof(string)),
        new ToolParameter("timeout_seconds", "Timeout in seconds (default: 60)", typeof(string)),
    };

    public TerminalTool(ILogger<TerminalTool> logger, ISecurityManager? securityManager = null)
    {
        _logger = logger;
        _security = securityManager;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("command", out var command);
        parameters.TryGetValue("working_directory", out var workingDir);
        parameters.TryGetValue("timeout_seconds", out var timeoutStr);

        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Failed("Parameter 'command' is required");

        if (_security is not null && _security.IsCommandBlacklisted(command))
            return ToolResult.Failed($"Command blocked by security policy: '{command}' contains blocked terms");

        if (_security is not null && _security.GetOptions().EnableCommandInjectionGuard
            && _security.GetOptions().Mode != OperationMode.Autonomous)
        {
            var injectionCheck = CommandInjectionGuard.Validate(command, _security.GetOptions());
            if (!injectionCheck.Allowed)
            {
                _logger.LogWarning("[TerminalTool] Command rejected by injection guard: {Reason} | Command='{Command}'",
                    injectionCheck.Reason, command);
                return ToolResult.Failed($"Command blocked by security policy: {injectionCheck.Reason}");
            }
        }

        int timeoutMs = 60000;
        if (!string.IsNullOrEmpty(timeoutStr) && int.TryParse(timeoutStr, out var timeoutSec))
            timeoutMs = Math.Min(timeoutSec * 1000, 300000);

        workingDir = string.IsNullOrWhiteSpace(workingDir) ? Directory.GetCurrentDirectory() : workingDir;

        if (_security is not null && !string.IsNullOrWhiteSpace(workingDir) && !_security.IsPathAllowed(workingDir))
            return ToolResult.Failed($"Working directory '{workingDir}' is outside allowed directories");

        return action?.ToLowerInvariant() switch
        {
            "execute_command" => await RunProcessAsync("cmd.exe", $"/c \"{command}\"", workingDir, timeoutMs, cancellationToken),
            "execute_powershell" => await RunProcessAsync("powershell.exe", $"-NoProfile -Command \"{command}\"", workingDir, timeoutMs, cancellationToken),
            _ => ToolResult.Failed($"Unknown action: {action}. Valid: execute_command, execute_powershell")
        };
    }

    private async Task<ToolResult> RunProcessAsync(string fileName, string arguments, string workingDir, int timeoutMs, CancellationToken ct)
    {
        _logger.LogInformation("[TerminalTool] Running: {FileName} {Arguments} in {WorkingDir}", fileName, arguments, workingDir);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            sw.Stop();
            return ToolResult.Failed($"Command timed out after {timeoutMs}ms");
        }
        sw.Stop();

        var output = outputBuilder.ToString().Trim();
        var error = errorBuilder.ToString().Trim();
        var exitCode = process.ExitCode;

        _logger.LogInformation("[TerminalTool] Exit code: {ExitCode}, Output: {OutputLen} chars, Errors: {ErrorLen} chars, Duration: {Duration}ms",
            exitCode, output.Length, error.Length, sw.ElapsedMilliseconds);

        if (exitCode == 0 && string.IsNullOrEmpty(error))
            return ToolResult.Succeeded(output.Length > 0 ? output : "(Command completed with no output)");

        if (exitCode == 0 && !string.IsNullOrEmpty(error))
            return ToolResult.Succeeded($"Command completed (exit code 0) with stderr output:\n{error}\n\nStdout:\n{output}");

        return ToolResult.Failed($"Command failed (exit code {exitCode}):\n{error}\n\nStdout:\n{output}");
    }
}
