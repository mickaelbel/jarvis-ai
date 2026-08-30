using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceUiService
{
    void ShowSubtitles(string text, bool isPartial = false);
    void HideSubtitles();
    void ShowWaveAnimation(bool isActive);
    void HideWaveAnimation();
    void ShowVoiceStatus(VoiceUiStatus status);
    void HideVoiceStatus();
    Task<VoiceUiConfig> GetConfigAsync();
    Task SaveConfigAsync(VoiceUiConfig config);
    event EventHandler<VoiceUiEventArgs>? OnUiEvent;
}

public sealed class VoiceUiService : IVoiceUiService
{
    private readonly ILogger<VoiceUiService> _logger;
    private readonly string _configPath;
    private VoiceUiConfig _config;
    private bool _subtitlesVisible;
    private bool _waveVisible;
    private bool _statusVisible;

    public event EventHandler<VoiceUiEventArgs>? OnUiEvent;

    public VoiceUiService(ILogger<VoiceUiService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "config", "voice_ui.json");
        _config = LoadConfig();
    }

    public void ShowSubtitles(string text, bool isPartial = false)
    {
        _subtitlesVisible = true;
        OnUiEvent?.Invoke(this, new VoiceUiEventArgs
        {
            EventType = VoiceUiEventType.SubtitleShown,
            Data = JsonSerializer.Serialize(new { text, isPartial })
        });
        _logger.LogDebug("[VoiceUI] Subtitle: {Text}", text[..Math.Min(50, text.Length)]);
    }

    public void HideSubtitles()
    {
        _subtitlesVisible = false;
        OnUiEvent?.Invoke(this, new VoiceUiEventArgs
        {
            EventType = VoiceUiEventType.SubtitleHidden
        });
    }

    public void ShowWaveAnimation(bool isActive)
    {
        _waveVisible = isActive;
        OnUiEvent?.Invoke(this, new VoiceUiEventArgs
        {
            EventType = isActive ? VoiceUiEventType.WaveStarted : VoiceUiEventType.WaveStopped
        });
    }

    public void HideWaveAnimation() => ShowWaveAnimation(false);

    public void ShowVoiceStatus(VoiceUiStatus status)
    {
        _statusVisible = true;
        OnUiEvent?.Invoke(this, new VoiceUiEventArgs
        {
            EventType = VoiceUiEventType.StatusChanged,
            Data = JsonSerializer.Serialize(status)
        });
    }

    public void HideVoiceStatus()
    {
        _statusVisible = false;
        OnUiEvent?.Invoke(this, new VoiceUiEventArgs
        {
            EventType = VoiceUiEventType.StatusHidden
        });
    }

    public Task<VoiceUiConfig> GetConfigAsync() => Task.FromResult(_config);

    public Task SaveConfigAsync(VoiceUiConfig config)
    {
        _config = config;
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch { }
        return Task.CompletedTask;
    }

    private VoiceUiConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<VoiceUiConfig>(json) ?? new VoiceUiConfig();
            }
        }
        catch { }

        return new VoiceUiConfig
        {
            ShowSubtitles = true,
            ShowWaveAnimation = true,
            ShowStatusIndicator = true,
            SubtitleFontSize = 16,
            SubtitleColor = "#FFFFFF",
            SubtitlePosition = "bottom",
            WaveColor = "#4A90D9",
            StatusPosition = "top-right",
            AutoHideDelayMs = 3000,
            EnableAnimations = true,
            Opacity = 0.9f
        };
    }
}

public sealed class VoiceUiConfig
{
    public bool ShowSubtitles { get; set; } = true;
    public bool ShowWaveAnimation { get; set; } = true;
    public bool ShowStatusIndicator { get; set; } = true;
    public int SubtitleFontSize { get; set; } = 16;
    public string SubtitleColor { get; set; } = "#FFFFFF";
    public string SubtitlePosition { get; set; } = "bottom";
    public string WaveColor { get; set; } = "#4A90D9";
    public string StatusPosition { get; set; } = "top-right";
    public int AutoHideDelayMs { get; set; } = 3000;
    public bool EnableAnimations { get; set; } = true;
    public float Opacity { get; set; } = 0.9f;
}

public sealed class VoiceUiEventArgs : EventArgs
{
    public VoiceUiEventType EventType { get; set; }
    public string? Data { get; set; }
}

public sealed class VoiceUiStatus
{
    public string State { get; set; } = "idle";
    public string? Message { get; set; }
    public float? Progress { get; set; }
}

public enum VoiceUiEventType
{
    SubtitleShown,
    SubtitleHidden,
    WaveStarted,
    WaveStopped,
    StatusChanged,
    StatusHidden
}
