using JarvisAI.Application.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Security;

public sealed class SecuritySandbox : ISecuritySandbox
{
    private readonly ILogger<SecuritySandbox> _logger;
    private readonly IAuditLogService? _audit;

    private static readonly string[] DangerousPatterns = new[]
    {
        "rm -rf", "rmdir /s", "del /f", "Remove-Item -Recurse -Force",
        "format", "diskpart", "shutdown", "restart",
        "reg delete", "Remove-ItemProperty",
        "taskkill /f", "Stop-Process -Force",
        "net user", "net localgroup",
        "icacls", "takeown",
    };

    private static readonly string[] RestrictedPatterns = new[]
    {
        "reg add", "reg edit", "Set-ItemProperty",
        "New-Item", "Set-Content", "Add-Content",
        "Install-Package", "choco install", "winget install",
        "netsh", "firewall",
    };

    private static readonly string[] ForbiddenPatterns = new[]
    {
        "format c:", "diskpart clean",
        "bcdedit", "bootsect",
        "cipher /w",
        "iisreset",
    };

    public SecuritySandbox(ILogger<SecuritySandbox> logger, IAuditLogService? audit = null)
    {
        _logger = logger;
        _audit = audit;
    }

    public bool IsDangerousCommand(string command)
    {
        var lower = command.ToLowerInvariant();
        return DangerousPatterns.Any(p => lower.Contains(p.ToLowerInvariant()));
    }

    public SandboxLevel GetRequiredLevel(string command)
    {
        var lower = command.ToLowerInvariant();

        if (ForbiddenPatterns.Any(p => lower.Contains(p.ToLowerInvariant())))
            return SandboxLevel.Forbidden;

        if (DangerousPatterns.Any(p => lower.Contains(p.ToLowerInvariant())))
            return SandboxLevel.Dangerous;

        if (RestrictedPatterns.Any(p => lower.Contains(p.ToLowerInvariant())))
            return SandboxLevel.Restricted;

        return SandboxLevel.Normal;
    }

    public async Task<SandboxResult> ExecuteInSandboxAsync(string command, SandboxOptions options, CancellationToken ct = default)
    {
        var level = GetRequiredLevel(command);
        var sw = Stopwatch.StartNew();

        _logger.LogInformation("[Sandbox] Executing: {Command} (Level: {Level}, Timeout: {Timeout}ms)",
            command, level, options.TimeoutMs);

        if (level == SandboxLevel.Forbidden)
        {
            _logger.LogWarning("[Sandbox] FORBIDDEN command blocked: {Command}", command);
            return new SandboxResult(false, "", "Commande interdite par la politique de sécurité.", -1, 0, false);
        }

        // Réduire le timeout pour les commandes dangereuses
        var effectiveTimeout = level == SandboxLevel.Dangerous
            ? Math.Min(options.TimeoutMs, 15000)
            : options.TimeoutMs;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                WorkingDirectory = options.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = options.CaptureOutput,
                RedirectStandardError = options.CaptureOutput,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi };
            var output = new StringBuilder();
            var error = new StringBuilder();
            var killed = false;

            if (options.CaptureOutput)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };
            }

            process.Start();

            if (options.CaptureOutput)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(effectiveTimeout);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); killed = true; } catch { }
            }

            sw.Stop();

            var result = new SandboxResult(
                process.ExitCode == 0,
                output.ToString().Trim(),
                error.ToString().Trim(),
                process.ExitCode,
                sw.ElapsedMilliseconds,
                killed);

            // Logger dans l'audit
            if (_audit is not null)
            {
                await _audit.LogAsync(new AuditEntry(
                    Guid.NewGuid(), DateTime.Now,
                    "sandbox", "execute",
                    command, result.Success,
                    result.Output, result.Error,
                    result.DurationMs, null));
            }

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SandboxResult(false, "", ex.Message, -1, sw.ElapsedMilliseconds, false);
        }
    }
}
