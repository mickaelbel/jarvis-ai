using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IAnimationService
{
    AnimationConfig GetConfig();
    void SetConfig(AnimationConfig config);
    IReadOnlyList<AnimationPreset> GetPresets();
    void ApplyPreset(string presetName);
    bool IsEnabled { get; }
}

public sealed class AnimationService : IAnimationService
{
    private readonly ILogger<AnimationService> _logger;
    private readonly string _storagePath;
    private AnimationConfig _config = new();

    public bool IsEnabled => _config.Enabled;

    public AnimationService(ILogger<AnimationService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "animations.json");
        Load();
    }

    public AnimationConfig GetConfig() => _config;

    public void SetConfig(AnimationConfig config)
    {
        _config = config;
        Save();
        _logger.LogInformation("[Animation] Config updated: Enabled={Enabled}, Duration={Duration}ms",
            config.Enabled, config.DefaultDurationMs);
    }

    public IReadOnlyList<AnimationPreset> GetPresets()
    {
        return new List<AnimationPreset>
        {
            new() { Name = "none", Description = "Pas d'animations", DurationMs = 0, Easing = "linear" },
            new() { Name = "subtle", Description = "Animations subtiles", DurationMs = 200, Easing = "ease-out" },
            new() { Name = "smooth", Description = "Animations fluides", DurationMs = 300, Easing = "cubic-bezier(0.4, 0, 0.2, 1)" },
            new() { Name = "bouncy", Description = "Animations rebondissantes", DurationMs = 400, Easing = "cubic-bezier(0.68, -0.55, 0.265, 1.55)" },
            new() { Name = "snappy", Description = "Animations rapides", DurationMs = 150, Easing = "ease-in-out" },
        };
    }

    public void ApplyPreset(string presetName)
    {
        var preset = GetPresets().FirstOrDefault(p => p.Name == presetName);
        if (preset is null) return;

        _config.DefaultDurationMs = preset.DurationMs;
        _config.Easing = preset.Easing;
        _config.Enabled = preset.Name != "none";
        Save();

        _logger.LogInformation("[Animation] Preset applied: {Name}", presetName);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _config = JsonSerializer.Deserialize<AnimationConfig>(json) ?? new();
            }
        }
        catch { _config = new(); }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class AnimationConfig
{
    public bool Enabled { get; set; } = true;
    public int DefaultDurationMs { get; set; } = 300;
    public string Easing { get; set; } = "cubic-bezier(0.4, 0, 0.2, 1)";
    public bool ReduceMotion { get; set; }
    public bool ShowTypingIndicator { get; set; } = true;
    public bool AnimateMessages { get; set; } = true;
    public bool AnimateOverlays { get; set; } = true;
    public bool AnimateThemeTransitions { get; set; } = true;
}

public sealed class AnimationPreset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int DurationMs { get; set; }
    public string Easing { get; set; } = "";
}
