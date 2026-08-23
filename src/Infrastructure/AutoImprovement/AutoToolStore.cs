using JarvisAI.Application.AutoImprovement;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AutoImprovement;

public sealed class AutoToolStore : IAutoToolStore
{
    private readonly string _toolsDir;
    private readonly string _settingsPath;
    private readonly string _lessonsPath;
    private readonly ILogger<AutoToolStore> _logger;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AutoToolStore(ILogger<AutoToolStore> logger, string? baseDir = null)
    {
        _logger = logger;
        baseDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI");
        var autoDir = Path.Combine(baseDir, "auto");
        _toolsDir = Path.Combine(autoDir, "tools");
        _settingsPath = Path.Combine(autoDir, "settings.json");
        _lessonsPath = Path.Combine(autoDir, "lessons.md");
    }

    public bool SafeMode
    {
        get
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_settingsPath)) return false;
                    using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                    return doc.RootElement.TryGetProperty("safeMode", out var sm) && sm.GetBoolean();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AutoToolStore] Failed to read safe mode");
                    return false;
                }
            }
        }
        set
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                    JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(_settingsPath, JsonSerializer.Serialize(new { safeMode = value }, JsonOptions));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AutoToolStore] Failed to write safe mode");
                }
            }
        }
    }

    public IReadOnlyList<AutoToolSpec> LoadTools()
    {
        lock (_lock)
        {
            var result = new List<AutoToolSpec>();
            if (!Directory.Exists(_toolsDir)) return result;

            foreach (var file in Directory.EnumerateFiles(_toolsDir, "*.json"))
            {
                try
                {
                    var spec = JsonSerializer.Deserialize<AutoToolSpec>(File.ReadAllText(file), JsonOptions);
                    if (spec is not null && !string.IsNullOrWhiteSpace(spec.Name))
                        result.Add(spec);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AutoToolStore] Failed to load auto-tool from {File}", file);
                }
            }
            return result;
        }
    }

    public AutoToolSpec? GetTool(string name)
    {
        lock (_lock)
        {
            var path = GetToolPath(name);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<AutoToolSpec>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AutoToolStore] Failed to load auto-tool {Name}", name);
                return null;
            }
        }
    }

    public void SaveTool(AutoToolSpec spec)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_toolsDir);
                JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(GetToolPath(spec.Name), JsonSerializer.Serialize(spec, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AutoToolStore] Failed to save auto-tool {Name}", spec.Name);
            }
        }
    }

    public void DeleteTool(string name)
    {
        lock (_lock)
        {
            try
            {
                var path = GetToolPath(name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AutoToolStore] Failed to delete auto-tool {Name}", name);
            }
        }
    }

    public IReadOnlyList<string> LoadLessons(int maxCount = 25)
    {
        lock (_lock)
        {
            if (!File.Exists(_lessonsPath)) return Array.Empty<string>();
            try
            {
                var lines = File.ReadAllLines(_lessonsPath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .TakeLast(maxCount)
                    .ToList();
                return lines;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AutoToolStore] Failed to load lessons");
                return Array.Empty<string>();
            }
        }
    }

    public void AppendLesson(string lesson)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_lessonsPath)!);
                var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {lesson.Replace('\r', ' ').Replace('\n', ' ')}";
                File.AppendAllText(_lessonsPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AutoToolStore] Failed to append lesson");
            }
        }
    }

    public void ClearLessons()
    {
        lock (_lock)
        {
            try { if (File.Exists(_lessonsPath)) File.Delete(_lessonsPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "[AutoToolStore] Failed to clear lessons"); }
        }
    }

    private string GetToolPath(string name) => Path.Combine(_toolsDir, $"{name}.json");
}
