using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceSystemCommandsService
{
    Task<VoiceCommandResult> ExecuteSystemCommandAsync(string command, CancellationToken ct = default);
    Task<VoiceCommandResult> ControlVolumeAsync(string action, int? level = null);
    Task<VoiceCommandResult> ControlMediaAsync(string action);
    Task<VoiceCommandResult> TakeScreenshotAsync();
    Task<VoiceCommandResult> OpenApplicationAsync(string appName);
    Task<VoiceCommandResult> CloseApplicationAsync(string appName);
    Task<VoiceCommandResult> MinimizeAllAsync();
    Task<VoiceCommandResult> GetSystemInfoAsync();
    IReadOnlyList<string> GetInstalledApplications();
}

public sealed class VoiceSystemCommandsService : IVoiceSystemCommandsService
{
    private readonly ILogger<VoiceSystemCommandsService> _logger;

    public VoiceSystemCommandsService(ILogger<VoiceSystemCommandsService> logger)
    {
        _logger = logger;
    }

    public async Task<VoiceCommandResult> ExecuteSystemCommandAsync(string command, CancellationToken ct = default)
    {
        var lower = command.ToLowerInvariant().Trim();

        // Power commands
        if (lower.Contains("éteins") || lower.Contains("arrête l'ordinateur"))
            return ExecuteProcess("shutdown", "/s /t 0");
        if (lower.Contains("redémarre"))
            return ExecuteProcess("shutdown", "/r /t 0");
        if (lower.Contains("verrouille") || lower.Contains("lock"))
            return ExecuteProcess("rundll32.exe", "user32.dll,LockWorkStation");
        if (lower.Contains("veille") || lower.Contains("sleep"))
            return ExecuteProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");

        // Volume commands
        if (lower.Contains("monte le son") || lower.Contains("augmente le volume"))
            return await ControlVolumeAsync("up");
        if (lower.Contains("baisse le son") || lower.Contains("diminue le volume"))
            return await ControlVolumeAsync("down");
        if (lower.Contains("coupe le son") || lower.Contains("mute"))
            return await ControlVolumeAsync("mute");
        if (lower.Contains("remet le son") || lower.Contains("unmute"))
            return await ControlVolumeAsync("unmute");

        // Media commands
        if (lower.Contains("pause") || lower.Contains("met en pause"))
            return await ControlMediaAsync("pause");
        if (lower.Contains("lecture") || lower.Contains("play"))
            return await ControlMediaAsync("play");
        if (lower.Contains("suivant") || lower.Contains("next"))
            return await ControlMediaAsync("next");
        if (lower.Contains("précédent") || lower.Contains("previous"))
            return await ControlMediaAsync("previous");

        // Window commands
        if (lower.Contains("screenshot") || lower.Contains("capture d'écran"))
            return await TakeScreenshotAsync();
        if (lower.Contains("minimise tout") || lower.Contains("show desktop"))
            return await MinimizeAllAsync();
        if (lower.Contains("informations système") || lower.Contains("system info"))
            return await GetSystemInfoAsync();

        // Application commands
        if (lower.Contains("ouvre") || lower.Contains("lance"))
        {
            var appName = ExtractAppName(lower);
            if (appName is not null)
                return await OpenApplicationAsync(appName);
        }

        if (lower.Contains("ferme"))
        {
            var appName = ExtractAppName(lower);
            if (appName is not null)
                return await CloseApplicationAsync(appName);
        }

        return new VoiceCommandResult
        {
            Success = false,
            Message = "Commande système non reconnue"
        };
    }

    public Task<VoiceCommandResult> ControlVolumeAsync(string action, int? level = null)
    {
        try
        {
            string nircmdAction;
            switch (action.ToLowerInvariant())
            {
                case "up":
                    nircmdAction = "setsysvolume 0x4000";
                    break;
                case "down":
                    nircmdAction = "setsysvolume 0x2000";
                    break;
                case "mute":
                    nircmdAction = "setsysvolume 0";
                    break;
                case "unmute":
                    nircmdAction = "setsysvolume 0x5000";
                    break;
                case "set" when level.HasValue:
                    nircmdAction = $"setsysvolume {level.Value * 655}";
                    break;
                default:
                    return Task.FromResult(new VoiceCommandResult { Success = false, Message = "Action de volume inconnue" });
            }

            ExecuteProcess("nircmd.exe", nircmdAction);
            return Task.FromResult(new VoiceCommandResult { Success = true, Message = $"Volume: {action}" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VoiceCommandResult { Success = false, Message = ex.Message });
        }
    }

    public Task<VoiceCommandResult> ControlMediaAsync(string action)
    {
        try
        {
            // Use Windows media controls via keyboard simulation
            string key;
            switch (action.ToLowerInvariant())
            {
                case "play":
                case "pause":
                    key = "{MEDIA_PLAY_PAUSE}";
                    break;
                case "next":
                    key = "{MEDIA_NEXT_TRACK}";
                    break;
                case "previous":
                    key = "{MEDIA_PREV_TRACK}";
                    break;
                case "stop":
                    key = "{MEDIA_STOP}";
                    break;
                default:
                    return Task.FromResult(new VoiceCommandResult { Success = false, Message = "Action média inconnue" });
            }

            // SendKeys is not available in .NET Core, use alternative
            ExecuteProcess("powershell.exe", $"-c \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('{key}')\"");
            return Task.FromResult(new VoiceCommandResult { Success = true, Message = $"Média: {action}" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VoiceCommandResult { Success = false, Message = ex.Message });
        }
    }

    public Task<VoiceCommandResult> TakeScreenshotAsync()
    {
        try
        {
            var screenshotDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "JarvisAI");
            Directory.CreateDirectory(screenshotDir);

            var filename = Path.Combine(screenshotDir, $"screenshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");

            // Use ffmpeg for screenshot
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-f gdigrab -i desktop -frames:v 1 \"{filename}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);

            if (File.Exists(filename))
            {
                return Task.FromResult(new VoiceCommandResult
                {
                    Success = true,
                    Message = $"Capture sauvegardée: {filename}",
                    Data = filename
                });
            }

            return Task.FromResult(new VoiceCommandResult { Success = false, Message = "Échec de la capture" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VoiceCommandResult { Success = false, Message = ex.Message });
        }
    }

    public Task<VoiceCommandResult> OpenApplicationAsync(string appName)
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
                return Task.FromResult(new VoiceCommandResult { Success = true, Message = $"{appName} lancé" });
            }
            catch
            {
                return Task.FromResult(new VoiceCommandResult { Success = false, Message = $"Impossible de lancer {appName}" });
            }
        }

        return Task.FromResult(new VoiceCommandResult { Success = false, Message = $"Application {appName} non trouvée" });
    }

    public Task<VoiceCommandResult> CloseApplicationAsync(string appName)
    {
        try
        {
            var processes = Process.GetProcessesByName(appName);
            foreach (var process in processes)
            {
                process.Kill();
            }
            return Task.FromResult(new VoiceCommandResult { Success = true, Message = $"{appName} fermé" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VoiceCommandResult { Success = false, Message = ex.Message });
        }
    }

    public Task<VoiceCommandResult> MinimizeAllAsync()
    {
        ExecuteProcess("powershell.exe", "-c \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('^{ESC}')\"");
        return Task.FromResult(new VoiceCommandResult { Success = true, Message = "Toutes les fenêtres minimisées" });
    }

    public Task<VoiceCommandResult> GetSystemInfoAsync()
    {
        var info = new
        {
            OS = Environment.OSVersion.ToString(),
            CPU = Environment.ProcessorCount + " cœurs",
            RAM = $"{Environment.WorkingSet / 1024 / 1024} MB utilisés",
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Date = DateTime.Now.ToString("dd/MM/yyyy")
        };

        return Task.FromResult(new VoiceCommandResult
        {
            Success = true,
            Message = $"Système: {info.CPU}, {info.RAM}, {info.OS}",
            Data = JsonSerializer.Serialize(info)
        });
    }

    public IReadOnlyList<string> GetInstalledApplications()
    {
        return new List<string>
        {
            "chrome", "firefox", "edge", "explorer", "notepad", "calculator",
            "blender", "vscode", "visual studio", "spotify", "discord",
            "terminal", "powershell", "cmd", "task manager", "settings", "paint"
        };
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
            return new VoiceCommandResult { Success = true, Message = "Commande exécutée" };
        }
        catch (Exception ex)
        {
            return new VoiceCommandResult { Success = false, Message = ex.Message };
        }
    }

    private string? ExtractAppName(string command)
    {
        var patterns = new[]
        {
            @"ouvre\s+(\w+)",
            @"lance\s+(\w+)",
            @"ferme\s+(\w+)",
            @"quitte\s+(\w+)"
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(command, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }
}
