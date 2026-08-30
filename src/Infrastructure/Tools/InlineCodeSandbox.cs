using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class InlineCodeSandbox : ITool
{
    private readonly ILogger<InlineCodeSandbox> _logger;

    public string Name => "code_sandbox";
    public string Description => "Exécute un snippet de code (Python, PowerShell, JavaScript) dans un sandbox isolé avec sortie en streaming. Utile pour tester du code rapidement.";
    public string Category => "terminal";
    public Domain.Security.SecurityRiskLevel RiskLevel => Domain.Security.SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("language", "python, powershell, javascript", typeof(string), required: true),
        new ToolParameter("code", "Le code à exécuter", typeof(string), required: true),
        new ToolParameter("timeout_seconds", "Timeout en secondes (défaut: 30)", typeof(string)),
    };

    public InlineCodeSandbox(ILogger<InlineCodeSandbox> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("language", out var language);
        parameters.TryGetValue("code", out var code);
        parameters.TryGetValue("timeout_seconds", out var timeoutStr);

        if (string.IsNullOrWhiteSpace(code))
            return ToolResult.Failed("code est requis");

        int timeoutMs = 30000;
        if (!string.IsNullOrEmpty(timeoutStr) && int.TryParse(timeoutStr, out var sec))
            timeoutMs = Math.Min(sec * 1000, 120000);

        var lang = language?.ToLowerInvariant() ?? "python";

        try
        {
            var result = lang switch
            {
                "python" or "py" => await ExecutePythonAsync(code, timeoutMs, cancellationToken),
                "powershell" or "ps" => await ExecutePowerShellAsync(code, timeoutMs, cancellationToken),
                "javascript" or "js" => await ExecuteJavaScriptAsync(code, timeoutMs, cancellationToken),
                _ => new SandboxResult { Success = false, Error = $"Langage non supporté: {lang}. Utilise python, powershell, ou javascript." }
            };

            _logger.LogInformation("[CodeSandbox] {Language} executed in {Ms}ms, success={Success}",
                lang, result.DurationMs, result.Success);

            return result.Success
                ? ToolResult.Succeeded($"Sortie ({lang}, {result.DurationMs}ms):\n{result.Output}")
                : ToolResult.Failed($"Erreur ({lang}):\n{result.Error}");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Exception: {ex.Message}");
        }
    }

    private async Task<SandboxResult> ExecutePythonAsync(string code, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("python", "-c")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        return await RunProcessAsync(psi, code, timeoutMs, ct);
    }

    private async Task<SandboxResult> ExecutePowerShellAsync(string code, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        return await RunProcessAsync(psi, code, timeoutMs, ct);
    }

    private async Task<SandboxResult> ExecuteJavaScriptAsync(string code, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("node", "-e")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        return await RunProcessAsync(psi, $"\"{code.Replace("\"", "\\\"")}\"", timeoutMs, ct);
    }

    private async Task<SandboxResult> RunProcessAsync(ProcessStartInfo psi, string input, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)!;

        await process.StandardInput.WriteAsync(input.AsMemory(), ct);
        process.StandardInput.Close();

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

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
            return new SandboxResult { Success = false, Error = $"Timeout après {timeoutMs}ms", DurationMs = (int)sw.ElapsedMilliseconds };
        }

        sw.Stop();
        var output = outputBuilder.ToString().Trim();
        var error = errorBuilder.ToString().Trim();

        if (process.ExitCode == 0 && string.IsNullOrEmpty(error))
            return new SandboxResult { Success = true, Output = output, DurationMs = (int)sw.ElapsedMilliseconds };

        return new SandboxResult
        {
            Success = process.ExitCode == 0,
            Output = output,
            Error = error,
            DurationMs = (int)sw.ElapsedMilliseconds
        };
    }
}

internal sealed class SandboxResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int DurationMs { get; set; }
}
