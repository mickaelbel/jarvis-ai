using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Automation;

public interface IDailyWorkflowService
{
    Task ExecuteMorningWorkflowAsync(CancellationToken ct = default);
    Task LaunchAppsAsync(IEnumerable<string> apps, CancellationToken ct = default);
    Task<string> GetWeatherAsync(CancellationToken ct = default);
    Task<string> GetAgendaAsync(CancellationToken ct = default);
    Task ScheduleMorningWorkflowAsync(int hour, int minute, CancellationToken ct = default);
    List<ScheduledTask> GetScheduledTasks();
    Task CancelScheduledTaskAsync(string taskId, CancellationToken ct = default);
}

public sealed class DailyWorkflowService : IDailyWorkflowService
{
    private readonly ILogger<DailyWorkflowService> _logger;
    private readonly string _configPath;
    private readonly List<ScheduledTask> _scheduledTasks = new();
    private readonly System.Threading.Timer _checkTimer;

    public DailyWorkflowService(ILogger<DailyWorkflowService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "config", "daily_workflow.json");
        LoadConfig();

        // Check every minute for scheduled tasks
        _checkTimer = new System.Threading.Timer(CheckScheduledTasks, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    public async Task ExecuteMorningWorkflowAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[DailyWorkflow] Starting morning workflow...");

        // 1. Launch apps
        var defaultApps = new[] { "chrome", "Discord", "Code" };
        await LaunchAppsAsync(defaultApps, ct);

        // 2. Get weather
        var weather = await GetWeatherAsync(ct);
        _logger.LogInformation("[DailyWorkflow] Weather: {Weather}", weather);

        // 3. Get agenda
        var agenda = await GetAgendaAsync(ct);
        _logger.LogInformation("[DailyWorkflow] Agenda: {Agenda}", agenda);

        _logger.LogInformation("[DailyWorkflow] Morning workflow completed.");
    }

    public async Task LaunchAppsAsync(IEnumerable<string> apps, CancellationToken ct = default)
    {
        foreach (var app in apps)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var success = await LaunchAppAsync(app);
                if (success)
                    _logger.LogInformation("[DailyWorkflow] Launched: {App}", app);
                else
                    _logger.LogWarning("[DailyWorkflow] Failed to launch: {App}", app);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DailyWorkflow] Error launching {App}", app);
            }

            await Task.Delay(1000, ct); // Stagger app launches
        }
    }

    public async Task<string> GetWeatherAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            // Use wttr.in for simple weather
            var response = await client.GetStringAsync("https://wttr.in/?format=%l:+%c+%t+%h+%w&lang=fr", ct);
            return response.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DailyWorkflow] Weather fetch failed");
            return "Métééo indisponible";
        }
    }

    public async Task<string> GetAgendaAsync(CancellationToken ct = default)
    {
        try
        {
            // Check if Google Calendar tool is available
            var calendarFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", "config", "google_calendar.json");

            if (!File.Exists(calendarFile))
                return "Pas de calendrier configuré. Utilise 'google_calendar' pour connecter.";

            return "Calendrier connecté. Vérifie l'onglet Rappels pour les événements.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DailyWorkflow] Agenda fetch failed");
            return "Agenda indisponible";
        }
    }

    public Task ScheduleMorningWorkflowAsync(int hour, int minute, CancellationToken ct = default)
    {
        var task = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = "Morning Workflow",
            Hour = hour,
            Minute = minute,
            Days = "Mon,Tue,Wed,Thu,Fri",
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _scheduledTasks.Add(task);
        SaveConfig();

        _logger.LogInformation("[DailyWorkflow] Scheduled morning workflow at {Hour}:{Minute}", hour, minute);
        return Task.CompletedTask;
    }

    public List<ScheduledTask> GetScheduledTasks() => _scheduledTasks.ToList();

    public Task CancelScheduledTaskAsync(string taskId, CancellationToken ct = default)
    {
        var task = _scheduledTasks.FirstOrDefault(t => t.Id == taskId);
        if (task is not null)
        {
            task.Enabled = false;
            SaveConfig();
            _logger.LogInformation("[DailyWorkflow] Cancelled task: {TaskId}", taskId);
        }
        return Task.CompletedTask;
    }

    private async Task<bool> LaunchAppAsync(string appName)
    {
        var commonPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            ["google chrome"] = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            ["firefox"] = @"C:\Program Files\Mozilla Firefox\firefox.exe",
            ["discord"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Update.exe"),
            ["code"] = @"C:\Users\belmi\AppData\Local\Programs\Microsoft VS Code\Code.exe",
            ["vscode"] = @"C:\Users\belmi\AppData\Local\Programs\Microsoft VS Code\Code.exe",
            ["spotify"] = @"C:\Users\belmi\AppData\Roaming\Spotify\Spotify.exe",
            ["notepad"] = "notepad.exe",
            ["explorer"] = "explorer.exe",
            ["blender"] = @"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe",
            ["steam"] = @"C:\Program Files (x86)\Steam\steam.exe",
            ["obs"] = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
        };

        // Try exact path first
        if (commonPaths.TryGetValue(appName, out var exactPath) && File.Exists(exactPath))
        {
            Process.Start(new ProcessStartInfo(exactPath) { UseShellExecute = true });
            return true;
        }

        // Try by name (Windows will find it)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = appName,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            // Try with .exe extension
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = appName + ".exe",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void CheckScheduledTasks(object? state)
    {
        var now = DateTime.Now;
        var currentDay = now.ToString("ddd");

        foreach (var task in _scheduledTasks.Where(t => t.Enabled))
        {
            if (task.Days.Contains(currentDay) && task.Hour == now.Hour && task.Minute == now.Minute)
            {
                _ = Task.Run(async () =>
                {
                    _logger.LogInformation("[DailyWorkflow] Executing scheduled task: {Name}", task.Name);
                    await ExecuteMorningWorkflowAsync();
                });
            }
        }
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var data = JsonSerializer.Deserialize<WorkflowConfig>(json);
                if (data?.ScheduledTasks is not null)
                    _scheduledTasks.AddRange(data.ScheduledTasks);
            }
        }
        catch { }
    }

    private void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new WorkflowConfig { ScheduledTasks = _scheduledTasks };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch { }
    }
}

public sealed class ScheduledTask
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Hour { get; set; }
    public int Minute { get; set; }
    public string Days { get; set; } = ""; // "Mon,Tue,Wed,Thu,Fri"
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal class WorkflowConfig
{
    public List<ScheduledTask> ScheduledTasks { get; set; } = new();
}
