using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceCommandProcessor
{
    Task<VoiceCommandResult> ProcessCommandAsync(string command, CancellationToken ct = default);
    IReadOnlyList<VoiceCommandDefinition> GetAvailableCommands();
    void RegisterCommand(VoiceCommandDefinition command);
    VoiceCommandResult ExecuteSystemAction(string action);
}

public sealed class VoiceCommandProcessor : IVoiceCommandProcessor
{
    private readonly ILogger<VoiceCommandProcessor> _logger;
    private readonly List<VoiceCommandDefinition> _commands = new();

    public VoiceCommandProcessor(ILogger<VoiceCommandProcessor> logger)
    {
        _logger = logger;
        RegisterDefaultCommands();
    }

    public async Task<VoiceCommandResult> ProcessCommandAsync(string command, CancellationToken ct = default)
    {
        var lower = command.ToLowerInvariant().Trim();

        foreach (var cmd in _commands)
        {
            foreach (var trigger in cmd.Triggers)
            {
                if (lower.Contains(trigger.ToLowerInvariant()))
                {
                    _logger.LogInformation("[VoiceCmd] Matched: {Trigger} -> {Action}", trigger, cmd.Action);
                    return await ExecuteCommandAsync(cmd, command, ct);
                }
            }
        }

        return new VoiceCommandResult
        {
            Success = false,
            Message = "Commande non reconnue",
            IsCommand = false
        };
    }

    public IReadOnlyList<VoiceCommandDefinition> GetAvailableCommands() => _commands.AsReadOnly();

    public void RegisterCommand(VoiceCommandDefinition command)
    {
        _commands.Add(command);
    }

    public VoiceCommandResult ExecuteSystemAction(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "shutdown" or "éteins" or "arrête l'ordinateur" => ExecuteProcess("shutdown", "/s /t 0"),
            "restart" or "redémarre" => ExecuteProcess("shutdown", "/r /t 0"),
            "lock" or "verrouille" => ExecuteProcess("rundll32.exe", "user32.dll,LockWorkStation"),
            "sleep" or "veille" => ExecuteProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
            "volume_up" or "monte le son" => ExecuteProcess("nircmd.exe", "setsysvolume 0x4000"),
            "volume_down" or "baisse le son" => ExecuteProcess("nircmd.exe", "setsysvolume 0x2000"),
            "mute" or "coupe le son" => ExecuteProcess("nircmd.exe", "setsysvolume 0"),
            _ => new VoiceCommandResult { Success = false, Message = "Action inconnue" }
        };
    }

    private async Task<VoiceCommandResult> ExecuteCommandAsync(VoiceCommandDefinition cmd, string fullCommand, CancellationToken ct)
    {
        try
        {
            return cmd.ActionType switch
            {
                CommandType.System => ExecuteSystemAction(cmd.Action),
                CommandType.Application => await LaunchApplicationAsync(cmd.Action),
                CommandType.File => await OpenFileAsync(fullCommand),
                CommandType.Browse => await OpenBrowserAsync(fullCommand),
                CommandType.Note => await SaveNoteAsync(fullCommand),
                _ => new VoiceCommandResult { Success = false, Message = "Type de commande non supporté" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VoiceCmd] Execution failed");
            return new VoiceCommandResult { Success = false, Message = ex.Message };
        }
    }

    private VoiceCommandResult ExecuteProcess(string filename, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filename,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return new VoiceCommandResult { Success = true, Message = "Action exécutée" };
        }
        catch (Exception ex)
        {
            return new VoiceCommandResult { Success = false, Message = ex.Message };
        }
    }

    private async Task<VoiceCommandResult> LaunchApplicationAsync(string appName)
    {
        var appMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = "chrome.exe",
            ["firefox"] = "firefox.exe",
            ["edge"] = "msedge.exe",
            ["explorer"] = "explorer.exe",
            ["notepad"] = "notepad.exe",
            ["calculator"] = "calc.exe",
            ["blender"] = @"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe",
            ["vscode"] = "code.exe",
            ["visual studio"] = "devenv.exe",
            ["spotify"] = "spotify.exe",
            ["discord"] = "discord.exe",
            ["terminal"] = "wt.exe",
            ["powershell"] = "powershell.exe",
            ["cmd"] = "cmd.exe",
            ["task manager"] = "taskmgr.exe",
            ["settings"] = "ms-settings:",
            ["paint"] = "mspaint.exe",
        };

        if (appMap.TryGetValue(appName, out var exePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
                return new VoiceCommandResult { Success = true, Message = $"{appName} lancé" };
            }
            catch
            {
                return new VoiceCommandResult { Success = false, Message = $"Impossible de lancer {appName}" };
            }
        }

        return new VoiceCommandResult { Success = false, Message = $"Application {appName} non trouvée" };
    }

    private async Task<VoiceCommandResult> OpenFileAsync(string command)
    {
        var match = System.Text.RegularExpressions.Regex.Match(command, @"ouvre\s+(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var fileName = match.Groups[1].Value.Trim();
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = true
                });
                return new VoiceCommandResult { Success = true, Message = $"{fileName} ouvert" };
            }
            catch
            {
                return new VoiceCommandResult { Success = false, Message = $"Fichier {fileName} non trouvé" };
            }
        }
        return new VoiceCommandResult { Success = false, Message = "Nom de fichier non spécifié" };
    }

    private async Task<VoiceCommandResult> OpenBrowserAsync(string command)
    {
        var match = System.Text.RegularExpressions.Regex.Match(command, @"(chercher|recherche|google|chrome)\s+(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var query = match.Groups[2].Value.Trim();
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}",
                UseShellExecute = true
            });
            return new VoiceCommandResult { Success = true, Message = $"Recherche: {query}" };
        }
        return new VoiceCommandResult { Success = false, Message = "Requête non spécifiée" };
    }

    private async Task<VoiceCommandResult> SaveNoteAsync(string command)
    {
        var match = System.Text.RegularExpressions.Regex.Match(command, @"(note|enregistre|mémorise)\s+(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var note = match.Groups[2].Value.Trim();
            var noteFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", "notes", $"note_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");

            var dir = Path.GetDirectoryName(noteFile);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(noteFile, note);
            return new VoiceCommandResult { Success = true, Message = "Note sauvegardée" };
        }
        return new VoiceCommandResult { Success = false, Message = "Note non spécifiée" };
    }

    private void RegisterDefaultCommands()
    {
        _commands.AddRange(new[]
        {
            new VoiceCommandDefinition
            {
                Name = "Arrêter l'ordinateur",
                Triggers = new[] { "arrête l'ordinateur", "éteins l'ordinateur", "shutdown" },
                Action = "shutdown",
                ActionType = CommandType.System
            },
            new VoiceCommandDefinition
            {
                Name = "Redémarrer",
                Triggers = new[] { "redémarre", "restart" },
                Action = "restart",
                ActionType = CommandType.System
            },
            new VoiceCommandDefinition
            {
                Name = "Ouvrir Chrome",
                Triggers = new[] { "ouvre chrome", "lance chrome" },
                Action = "chrome",
                ActionType = CommandType.Application
            },
            new VoiceCommandDefinition
            {
                Name = "Ouvrir Blender",
                Triggers = new[] { "ouvre blender", "lance blender" },
                Action = "blender",
                ActionType = CommandType.Application
            },
            new VoiceCommandDefinition
            {
                Name = "Screenshot",
                Triggers = new[] { "screenshot", "capture d'écran", "prends une capture" },
                Action = "screenshot",
                ActionType = CommandType.System
            },
            new VoiceCommandDefinition
            {
                Name = "Volume",
                Triggers = new[] { "monte le son", "baisse le son", "coupe le son", "mute" },
                Action = "volume",
                ActionType = CommandType.System
            },
        });
    }
}

public sealed class VoiceCommandDefinition
{
    public string Name { get; set; } = "";
    public string[] Triggers { get; set; } = Array.Empty<string>();
    public string Action { get; set; } = "";
    public CommandType ActionType { get; set; }
}

public sealed class VoiceCommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public bool IsCommand { get; set; } = true;
    public string? Data { get; set; }
}

public enum CommandType
{
    System,
    Application,
    File,
    Browse,
    Note,
    Media,
    Calendar,
    Email,
    Weather,
    Calculation
}
