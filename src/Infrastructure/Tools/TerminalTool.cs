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
    private static readonly List<(string Command, DateTime Time, int ExitCode, string Output)> _history = new();
    private static readonly object _historyLock = new();

    public string Name => "terminal";
    public string Description => "Exécute des commandes CMD/PowerShell, gère l'historique, crée et lance des scripts. Actions: execute_command (CMD), execute_powershell (PS), get_history (dernières commandes), run_script (fichier .ps1/.bat/.cmd), compile (dotnet build, gcc, etc.), create_script (créer un fichier script), save_and_run (créer + exécuter en une étape). Utilise pour tout ce qui est terminal, scripts, compilation, git, npm, dotnet.";
    public string Category => "terminal";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "execute_command, execute_powershell, get_history, run_script, compile, create_script, save_and_run", typeof(string), required: true),
        new ToolParameter("command", "La commande à exécuter (execute_command/execute_powershell/compile)", typeof(string)),
        new ToolParameter("script_path", "Chemin du fichier script à exécuter/créer (run_script/create_script/save_and_run)", typeof(string)),
        new ToolParameter("script_content", "Contenu du script à créer (create_script/save_and_run)", typeof(string)),
        new ToolParameter("script_type", "Type de script: powershell, batch, python (create_script/save_and_run). Défaut: powershell", typeof(string)),
        new ToolParameter("working_directory", "Répertoire de travail (optionnel)", typeof(string)),
        new ToolParameter("timeout_seconds", "Timeout en secondes (défaut: 60, max: 300)", typeof(string)),
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
        parameters.TryGetValue("script_path", out var scriptPath);
        parameters.TryGetValue("script_content", out var scriptContent);
        parameters.TryGetValue("script_type", out var scriptType);
        parameters.TryGetValue("working_directory", out var workingDir);
        parameters.TryGetValue("timeout_seconds", out var timeoutStr);

        int timeoutMs = 60000;
        if (!string.IsNullOrEmpty(timeoutStr) && int.TryParse(timeoutStr, out var timeoutSec))
            timeoutMs = Math.Min(timeoutSec * 1000, 300000);

        workingDir = string.IsNullOrWhiteSpace(workingDir) ? Directory.GetCurrentDirectory() : workingDir;

        if (_security is not null && !_security.IsPathAllowed(workingDir))
            return ToolResult.Failed($"Répertoire de travail hors des dossiers autorisés: '{workingDir}'");

        return action?.ToLowerInvariant() switch
        {
            "execute_command" => await ExecuteCommandAsync(command, workingDir, timeoutMs, cancellationToken),
            "execute_powershell" => await ExecutePowerShellAsync(command, workingDir, timeoutMs, cancellationToken),
            "get_history" => GetHistory(),
            "run_script" => await RunScriptAsync(scriptPath, workingDir, timeoutMs, cancellationToken),
            "compile" => await CompileAsync(command, workingDir, timeoutMs, cancellationToken),
            "create_script" => CreateScript(scriptPath, scriptContent, scriptType ?? "powershell"),
            "save_and_run" => await SaveAndRunAsync(scriptPath, scriptContent, scriptType ?? "powershell", workingDir, timeoutMs, cancellationToken),
            _ => ToolResult.Failed($"Action inconnue: {action}. Valides: execute_command, execute_powershell, get_history, run_script, compile, create_script, save_and_run")
        };
    }

    private async Task<ToolResult> ExecuteCommandAsync(string? command, string workingDir, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Failed("Paramètre 'command' requis");

        if (_security is not null && _security.IsCommandBlacklisted(command))
            return ToolResult.Failed($"Commande bloquée par la politique de sécurité: '{command}'");

        if (_security is not null && _security.GetOptions().EnableCommandInjectionGuard
            && _security.GetOptions().Mode != OperationMode.Autonomous)
        {
            var injectionCheck = CommandInjectionGuard.Validate(command, _security.GetOptions());
            if (!injectionCheck.Allowed)
                return ToolResult.Failed($"Commande bloquée: {injectionCheck.Reason}");
        }

        var result = await RunProcessAsync("cmd.exe", $"/c \"{command}\"", workingDir, timeoutMs, ct);
        AddToHistory(command, result);
        return result;
    }

    private async Task<ToolResult> ExecutePowerShellAsync(string? command, string workingDir, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Failed("Paramètre 'command' requis");

        if (_security is not null && _security.IsCommandBlacklisted(command))
            return ToolResult.Failed($"Commande bloquée par la politique de sécurité: '{command}'");

        var result = await RunProcessAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"", workingDir, timeoutMs, ct);
        AddToHistory(command, result);
        return result;
    }

    private async Task<ToolResult> RunScriptAsync(string? scriptPath, string workingDir, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            return ToolResult.Failed("Paramètre 'script_path' requis");

        if (!File.Exists(scriptPath))
            return ToolResult.Failed($"Script non trouvé: {scriptPath}");

        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        return ext switch
        {
            ".ps1" => await RunProcessAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"", workingDir, timeoutMs, ct),
            ".bat" or ".cmd" => await RunProcessAsync("cmd.exe", $"/c \"{scriptPath}\"", workingDir, timeoutMs, ct),
            ".sh" => await RunProcessAsync("bash", $"\"{scriptPath}\"", workingDir, timeoutMs, ct),
            _ => ToolResult.Failed($"Type de script non supporté: {ext}")
        };
    }

    private async Task<ToolResult> CompileAsync(string? command, string workingDir, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Failed("Paramètre 'command' requis (ex: 'dotnet build', 'gcc main.c -o main')");

        // Détecter le compilateur et exécuter
        var lower = command.ToLowerInvariant();
        if (lower.Contains("dotnet"))
            return await RunProcessAsync("dotnet.exe", command["dotnet".Length..].Trim(), workingDir, timeoutMs, ct);
        if (lower.Contains("gcc") || lower.Contains("g++"))
            return await RunProcessAsync("gcc.exe", command["gcc".Length..].Trim(), workingDir, timeoutMs, ct);
        if (lower.Contains("npm"))
            return await RunProcessAsync("npm.exe", command, workingDir, timeoutMs, ct);
        if (lower.Contains("cargo"))
            return await RunProcessAsync("cargo.exe", command["cargo".Length..].Trim(), workingDir, timeoutMs, ct);

        // Fallback: exécuter telle quelle
        return await RunProcessAsync("cmd.exe", $"/c \"{command}\"", workingDir, timeoutMs, ct);
    }

    private static ToolResult GetHistory()
    {
        lock (_historyLock)
        {
            if (_history.Count == 0)
                return ToolResult.Succeeded("Aucune commande en historique.");

            var sb = new StringBuilder();
            sb.AppendLine($"Historique ({_history.Count} commandes) :");
            var recent = _history.TakeLast(20).ToList();
            for (int i = 0; i < recent.Count; i++)
            {
                var (cmd, time, exit, output) = recent[i];
                var status = exit == 0 ? "✓" : "✗";
                sb.AppendLine($"  {status} [{time:HH:mm:ss}] {cmd}");
                if (!string.IsNullOrEmpty(output) && output.Length <= 200)
                    sb.AppendLine($"    → {output}");
            }
            return ToolResult.Succeeded(sb.ToString());
        }
    }

    private static void AddToHistory(string command, ToolResult result)
    {
        lock (_historyLock)
        {
            _history.Add((command, DateTime.Now, result.Success ? 0 : 1, result.Output ?? ""));
            if (_history.Count > 100)
                _history.RemoveRange(0, _history.Count - 100);
        }
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
            return ToolResult.Failed($"Timeout après {timeoutMs}ms");
        }
        sw.Stop();

        var output = outputBuilder.ToString().Trim();
        var error = errorBuilder.ToString().Trim();
        var exitCode = process.ExitCode;

        _logger.LogInformation("[TerminalTool] Exit: {ExitCode}, Output: {Len} chars, Duration: {Duration}ms",
            exitCode, output.Length, sw.ElapsedMilliseconds);

        if (exitCode == 0 && string.IsNullOrEmpty(error))
            return ToolResult.Succeeded(output.Length > 0 ? output : "(Commande exécutée sans erreur)");

        if (exitCode == 0 && !string.IsNullOrEmpty(error))
            return ToolResult.Succeeded($"Commande OK (exit 0) avec stderr:\n{error}\n\nStdout:\n{output}");

        return ToolResult.Failed($"Commande échouée (exit {exitCode}):\n{error}\n\nStdout:\n{output}");
    }

    private ToolResult CreateScript(string? scriptPath, string? content, string scriptType)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            return ToolResult.Failed("script_path est requis pour create_script");

        if (string.IsNullOrWhiteSpace(content))
            return ToolResult.Failed("script_content est requis pour create_script");

        var dir = Path.GetDirectoryName(scriptPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var finalPath = scriptType.ToLowerInvariant() switch
        {
            "python" or "py" => scriptPath.EndsWith(".py") ? scriptPath : scriptPath + ".py",
            "batch" or "bat" or "cmd" => scriptPath.EndsWith(".bat") ? scriptPath : scriptPath + ".bat",
            _ => scriptPath.EndsWith(".ps1") ? scriptPath : scriptPath + ".ps1"
        };

        File.WriteAllText(finalPath, content, Encoding.UTF8);

        return ToolResult.Succeeded($"Script créé: {finalPath} ({content.Length} caractères, type: {scriptType})");
    }

    private async Task<ToolResult> SaveAndRunAsync(string? scriptPath, string? content, string scriptType, string workingDir, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            return ToolResult.Failed("script_path est requis pour save_and_run");

        if (string.IsNullOrWhiteSpace(content))
            return ToolResult.Failed("script_content est requis pour save_and_run");

        var createResult = CreateScript(scriptPath, content, scriptType);
        if (!createResult.Success) return createResult;

        var finalPath = scriptType.ToLowerInvariant() switch
        {
            "python" or "py" => scriptPath.EndsWith(".py") ? scriptPath : scriptPath + ".py",
            "batch" or "bat" or "cmd" => scriptPath.EndsWith(".bat") ? scriptPath : scriptPath + ".bat",
            _ => scriptPath.EndsWith(".ps1") ? scriptPath : scriptPath + ".ps1"
        };

        _logger.LogInformation("[TerminalTool] Save and run: {Path} (type: {Type})", finalPath, scriptType);

        return scriptType.ToLowerInvariant() switch
        {
            "python" or "py" => await ExecuteCommandAsync($"python \"{finalPath}\"", workingDir, timeoutMs, ct),
            "batch" or "bat" or "cmd" => await ExecuteCommandAsync(finalPath, workingDir, timeoutMs, ct),
            _ => await RunScriptAsync(finalPath, workingDir, timeoutMs, ct)
        };
    }
}
