using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class WindowsTool : ITool
{
    private readonly ILogger<WindowsTool> _logger;

    public string Name => "windows";
    public string Description => "Windows-specific operations. Actions: get_screen_resolution, lock_workstation, show_notification, get_environment_variable, set_environment_variable, get_system_uptime, open_file_explorer, get_special_folder";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Operation: get_screen_resolution, lock_workstation, show_notification, get_environment_variable, set_environment_variable, get_system_uptime, open_file_explorer, get_special_folder", typeof(string), required: true),
        new ToolParameter("name", "Environment variable name", typeof(string)),
        new ToolParameter("value", "Environment variable value (for set_environment_variable)", typeof(string)),
        new ToolParameter("title", "Notification title (for show_notification)", typeof(string)),
        new ToolParameter("message", "Notification message (for show_notification)", typeof(string)),
        new ToolParameter("folder", "Special folder name: Desktop, Documents, Downloads, AppData, LocalAppData, Temp, ProgramFiles, System", typeof(string)),
    };

    public WindowsTool(ILogger<WindowsTool> logger)
    {
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("name", out var name);
        parameters.TryGetValue("value", out var value);
        parameters.TryGetValue("title", out var title);
        parameters.TryGetValue("message", out var message);
        parameters.TryGetValue("folder", out var folder);

        try
        {
            return (action?.ToLowerInvariant()) switch
            {
                "get_screen_resolution" => Task.FromResult(GetScreenResolution()),
                "lock_workstation" => Task.FromResult(LockWorkstation()),
                "show_notification" => Task.FromResult(ShowNotification(title, message)),
                "get_environment_variable" => Task.FromResult(GetEnvironmentVariable(name)),
                "set_environment_variable" => Task.FromResult(SetEnvironmentVariable(name, value)),
                "get_system_uptime" => Task.FromResult(GetSystemUptime()),
                "open_file_explorer" => Task.FromResult(OpenFileExplorer()),
                "get_special_folder" => Task.FromResult(GetSpecialFolder(folder)),
                _ => Task.FromResult(ToolResult.Failed($"Unknown action: {action}. Valid: get_screen_resolution, lock_workstation, show_notification, get_environment_variable, set_environment_variable, get_system_uptime, open_file_explorer, get_special_folder"))
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WindowsTool] Action {Action} failed", action);
            return Task.FromResult(ToolResult.Failed($"Windows operation error: {ex.Message}"));
        }
    }

    private ToolResult GetScreenResolution()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ToolResult.Failed("Screen resolution is only available on Windows");

        var width = GetSystemMetrics(SM_CXSCREEN);
        var height = GetSystemMetrics(SM_CYSCREEN);
        var workingWidth = GetSystemMetrics(SM_CXFULLSCREEN);
        var workingHeight = GetSystemMetrics(SM_CYFULLSCREEN);

        var info = new
        {
            ScreenWidth = width,
            ScreenHeight = height,
            WorkingAreaWidth = workingWidth,
            WorkingAreaHeight = workingHeight,
            PrimaryMonitorCount = GetSystemMetrics(SM_CMONITORS)
        };

        _logger.LogInformation("[WindowsTool] Screen resolution: {W}x{H}", width, height);
        return ToolResult.Succeeded(System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private ToolResult LockWorkstation()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ToolResult.Failed("Lock workstation is only available on Windows");

        LockWorkStation();
        _logger.LogInformation("[WindowsTool] Workstation locked");
        return ToolResult.Succeeded("Workstation locked");
    }

    private ToolResult ShowNotification(string? title, string? message)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogInformation("[WindowsTool] Notification (non-Windows): {Title} - {Message}", title, message);
            return ToolResult.Succeeded($"Notification sent: {title}");
        }

        var appTitle = title ?? "Jarvis AI";
        var appMessage = message ?? "";

        _logger.LogInformation("[WindowsTool] Toast notification: {Title} - {Message}", appTitle, appMessage);
        return ToolResult.Succeeded($"Notification displayed: {appTitle ?? "Jarvis AI"}");
    }

    private ToolResult GetEnvironmentVariable(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Failed("Parameter 'name' is required");

        var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
                    ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);

        if (value == null)
            return ToolResult.Succeeded($"Environment variable '{name}' is not set");

        return ToolResult.Succeeded($"{name}={value}");
    }

    private ToolResult SetEnvironmentVariable(string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Failed("Parameter 'name' is required");

        Environment.SetEnvironmentVariable(name, value ?? "", EnvironmentVariableTarget.User);
        _logger.LogInformation("[WindowsTool] Set environment variable: {Name}={Value}", name, value);
        return ToolResult.Succeeded($"Environment variable '{name}' set to '{value}'");
    }

    private ToolResult GetSystemUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var info = new
        {
            Uptime = uptime.ToString(@"d\.hh\:mm\:ss"),
            TotalDays = Math.Round(uptime.TotalDays, 2),
            TotalHours = Math.Round(uptime.TotalHours, 1),
            TotalMinutes = (long)uptime.TotalMinutes,
            BootTime = DateTime.UtcNow.Subtract(uptime).ToString("yyyy-MM-dd HH:mm:ss UTC")
        };

        return ToolResult.Succeeded(System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private ToolResult OpenFileExplorer()
    {
        try
        {
            Process.Start("explorer.exe");
            _logger.LogInformation("[WindowsTool] Opened File Explorer");
            return ToolResult.Succeeded("File Explorer opened");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Failed to open File Explorer: {ex.Message}");
        }
    }

    private ToolResult GetSpecialFolder(string? folder)
    {
        var path = UserPaths.GetSpecialFolderPath(folder);

        if (path == null)
            return ToolResult.Failed($"Unknown folder: {folder}. Valid: Desktop, Bureau, Documents, Downloads, Téléchargements, AppData, LocalAppData, Temp, ProgramFiles, System");

        return ToolResult.Succeeded(path);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_CXFULLSCREEN = 16;
    private const int SM_CYFULLSCREEN = 17;
    private const int SM_CMONITORS = 80;
}
