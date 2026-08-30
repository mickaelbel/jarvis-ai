using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Web.Services;

public interface ICodePlaygroundService
{
    Task<PlaygroundResult> ExecuteCodeAsync(string code, string language, CancellationToken ct = default);
    IReadOnlyList<PlaygroundSession> GetSessions();
    PlaygroundSession? GetSession(string sessionId);
    void ClearSession(string sessionId);
}

public sealed class CodePlaygroundService : ICodePlaygroundService
{
    private readonly ILogger<CodePlaygroundService> _logger;
    private readonly Dictionary<string, PlaygroundSession> _sessions = new();

    public CodePlaygroundService(ILogger<CodePlaygroundService> logger)
    {
        _logger = logger;
    }

    public async Task<PlaygroundResult> ExecuteCodeAsync(string code, string language, CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var lang = language.ToLowerInvariant();

        var session = new PlaygroundSession
        {
            Id = sessionId,
            Language = language,
            Code = code,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var result = lang switch
            {
                "csharp" or "cs" => await ExecuteCSharp(code, ct),
                "python" or "py" => await ExecutePython(code, ct),
                "powershell" or "ps1" => await ExecutePowerShell(code, ct),
                "javascript" or "js" => await ExecuteJavaScript(code, ct),
                _ => new PlaygroundResult { Success = false, Error = $"Langage non supporté: {language}" }
            };

            session.Output = result.Output;
            session.Error = result.Error;
            session.Duration = result.Duration;

            _sessions[sessionId] = session;

            _logger.LogInformation("[Playground] {Lang} executed in {Duration}ms", language, result.Duration.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            session.Error = ex.Message;
            _sessions[sessionId] = session;

            return new PlaygroundResult
            {
                Success = false,
                Error = ex.Message,
                Duration = TimeSpan.Zero
            };
        }
    }

    public IReadOnlyList<PlaygroundSession> GetSessions()
        => _sessions.Values.OrderByDescending(s => s.CreatedAt).ToList();

    public PlaygroundSession? GetSession(string sessionId)
        => _sessions.GetValueOrDefault(sessionId);

    public void ClearSession(string sessionId)
    {
        _sessions.Remove(sessionId);
    }

    private async Task<PlaygroundResult> ExecuteCSharp(string code, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"playground_{Guid.NewGuid():N}.csx");
        try
        {
            await File.WriteAllTextAsync(tempFile, code, ct);

            var sw = Stopwatch.StartNew();
            var output = await RunProcessAsync("dotnet-script", tempFile, ct);
            sw.Stop();

            return new PlaygroundResult
            {
                Success = true,
                Output = output,
                Duration = sw.Elapsed
            };
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private async Task<PlaygroundResult> ExecutePython(string code, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"playground_{Guid.NewGuid():N}.py");
        try
        {
            await File.WriteAllTextAsync(tempFile, code, ct);

            var sw = Stopwatch.StartNew();
            var output = await RunProcessAsync("python", tempFile, ct);
            sw.Stop();

            return new PlaygroundResult
            {
                Success = true,
                Output = output,
                Duration = sw.Elapsed
            };
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private async Task<PlaygroundResult> ExecutePowerShell(string code, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var output = await RunProcessAsync("powershell", $"-Command \"{code.Replace("\"", "\\\"")}\"", ct);
        sw.Stop();

        return new PlaygroundResult
        {
            Success = true,
            Output = output,
            Duration = sw.Elapsed
        };
    }

    private async Task<PlaygroundResult> ExecuteJavaScript(string code, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"playground_{Guid.NewGuid():N}.js");
        try
        {
            await File.WriteAllTextAsync(tempFile, code, ct);

            var sw = Stopwatch.StartNew();
            var output = await RunProcessAsync("node", tempFile, ct);
            sw.Stop();

            return new PlaygroundResult
            {
                Success = true,
                Output = output,
                Duration = sw.Elapsed
            };
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null) return "Erreur: impossible de démarrer le processus";

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (!string.IsNullOrEmpty(stderr))
            return $"Sortie:\n{stdout}\n\nErreurs:\n{stderr}";

        return stdout;
    }
}

public sealed class PlaygroundResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed class PlaygroundSession
{
    public string Id { get; set; } = "";
    public string Language { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Output { get; set; }
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime CreatedAt { get; set; }
}
