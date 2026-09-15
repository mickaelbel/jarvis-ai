using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Blender;

public sealed record BlenderRunResult(string StandardOutput, string StandardError, int ExitCode, string? JsonOutputPath);

/// <summary>
/// Exécute un script bpy dans Blender en mode headless (--background). Blender ne
/// volant jamais le focus, l'opération reste discrète en arrière-plan pendant que
/// l'utilisateur travaille sur autre chose. Le script écrit son résultat JSON dans
/// un fichier temporaire (plus fiable que le parsing de stdout, pollué par Blender).
/// </summary>
public interface IBlenderScriptRunner
{
    Task<BlenderRunResult> RunAsync(string pythonBody, string jsonOutputPath, CancellationToken cancellationToken);
}

public sealed class BlenderProcessRunner : IBlenderScriptRunner
{
    private readonly ILogger _logger;

    public BlenderProcessRunner(ILogger<BlenderProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<BlenderRunResult> RunAsync(string pythonBody, string jsonOutputPath, CancellationToken cancellationToken)
    {
        var blenderExe = FindBlender();
        if (blenderExe is null)
            throw new BlenderNotFoundException("Blender introuvable — installe-le sous C:\\Program Files\\Blender Foundation");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"jarvis_blender_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, pythonBody, cancellationToken);

        var psi = new ProcessStartInfo
        {
            FileName = blenderExe,
            Arguments = $"--background --python \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Impossible de démarrer Blender.");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        try { File.Delete(scriptPath); } catch { /* best-effort */ }

        var jsonPath = File.Exists(jsonOutputPath) ? jsonOutputPath : null;
        if (jsonPath is null)
            _logger.LogWarning("[Blender] Aucun résultat JSON produit (code {Code}). stderr : {Stderr}",
                process.ExitCode, Truncate(stderr, 500));

        return new BlenderRunResult(stdout, stderr, process.ExitCode, jsonPath);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + "…";

    private static string? FindBlender()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (Directory.Exists(root))
        {
            var candidates = Directory.GetDirectories(root)
                .Where(d => Path.GetFileName(d).StartsWith("Blender "))
                .OrderByDescending(d => Path.GetFileName(d));
            foreach (var dir in candidates)
            {
                var exe = Path.Combine(dir, "blender.exe");
                if (File.Exists(exe))
                    return exe;
            }
        }

        // Fallback : Blender sur le PATH (installations manuelles).
        var onPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in onPath.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var exe = Path.Combine(entry.Trim('"'), "blender.exe");
            if (File.Exists(exe))
                return exe;
        }

        return null;
    }
}

public sealed class BlenderNotFoundException : Exception
{
    public BlenderNotFoundException(string message) : base(message) { }
}