using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public sealed class VoiceSettingsStore : IVoiceSettingsStore
{
    private readonly ILogger<VoiceSettingsStore> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();
    private VoiceSettings? _cached;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public VoiceSettingsStore(ILogger<VoiceSettingsStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI",
            "voice-settings.json");
    }

    public VoiceSettings Get()
    {
        lock (_lock)
        {
            if (_cached is not null) return Clone(_cached);

            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var loaded = JsonSerializer.Deserialize<VoiceSettings>(json, JsonOptions);
                    if (loaded is not null)
                    {
                        _cached = loaded;
                        return Clone(loaded);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceSettings] Failed to load settings from {Path}", _filePath);
            }

            _cached = new VoiceSettings();
            return Clone(_cached);
        }
    }

    public void Save(VoiceSettings settings)
    {
        lock (_lock)
        {
            _cached = Clone(settings);
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceSettings] Failed to save settings to {Path}", _filePath);
            }
        }
    }

    private static VoiceSettings Clone(VoiceSettings settings)
    {
        var clone = new VoiceSettings();
        clone.VoiceEnabled = settings.VoiceEnabled;
        clone.WakeWordEnabled = settings.WakeWordEnabled;
        clone.PassiveMode = settings.PassiveMode;
        clone.WakeWords = settings.WakeWords;
        clone.BargeInEnabled = settings.BargeInEnabled;
        clone.MicDeviceId = settings.MicDeviceId;
        clone.SpeakerDeviceId = settings.SpeakerDeviceId;
        clone.TtsVoice = settings.TtsVoice;
        clone.TtsLanguage = settings.TtsLanguage;
        clone.SttLanguage = settings.SttLanguage;
        clone.Volume = settings.Volume;
        clone.TtsSpeed = settings.TtsSpeed;
        clone.AutoStart = settings.AutoStart;
        clone.SilenceTimeoutMs = settings.SilenceTimeoutMs;
        clone.VadThreshold = settings.VadThreshold;
        clone.MaxUtteranceSeconds = settings.MaxUtteranceSeconds;
        clone.Model = settings.Model;
        return clone;
    }
}
