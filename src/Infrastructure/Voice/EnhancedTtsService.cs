using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IEnhancedTtsService
{
    Task<byte[]> SynthesizeAsync(string text, TtsOptions? options = null, CancellationToken ct = default);
    Task<IReadOnlyList<VoicePreset>> GetVoicesAsync();
    void SetEmotion(string emotion);
    void SetSpeed(float speed);
    void SetVolume(float volume);
    TtsConfig GetConfig();
    void SaveConfig(TtsConfig config);
    Task<byte[]> SynthesizeWithEffectsAsync(string text, VoiceEffect effect, CancellationToken ct = default);
    IReadOnlyList<VoicePreset> GetDefaultVoices();
}

public sealed class EnhancedTtsService : IEnhancedTtsService, IAsyncDisposable
{
    private readonly ILogger<EnhancedTtsService> _logger;
    private readonly string _configPath;
    private TtsConfig _config;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly SemaphoreSlim _synthLock = new(1, 1);

    // French emotion mappings
    private static readonly Dictionary<string, (float rate, float pitch)> EmotionProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["neutral"] = (0f, 0f),
        ["happy"] = (10f, 5f),
        ["sad"] = (-10f, -3f),
        ["excited"] = (20f, 8f),
        ["calm"] = (-5f, -2f),
        ["serious"] = (-8f, -4f),
        ["empathetic"] = (-12f, -5f),
        ["urgent"] = (15f, 6f),
        ["whisper"] = (-15f, -8f),
        ["enthusiastic"] = (15f, 10f),
        ["angry"] = (12f, 7f),
        ["surprised"] = (8f, 12f),
    };

    // French pronunciation rules
    private static readonly Dictionary<string, string> PronunciationOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AI"] = "A I",
        ["API"] = "A Pé I",
        ["HTTP"] = "Ach Té Té Pé",
        ["URL"] = "U R El",
        ["GPU"] = "Jé Pé U",
        ["CPU"] = "Cé Pé U",
        ["HTML"] = "Ach Té Em El",
        ["JSON"] = "Jé Son",
        ["NVIDIA"] = "Nvidia",
        ["CUDA"] = "Cou Dah",
        ["Blender"] = "Blendeur",
        ["Python"] = "Pithon",
        ["JavaScript"] = "Java Script",
    };

    public EnhancedTtsService(ILogger<EnhancedTtsService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "config", "tts.json");
        _config = LoadConfig();
    }

    public async Task<byte[]> SynthesizeAsync(string text, TtsOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        var sw = Stopwatch.StartNew();

        // Apply pronunciation overrides
        text = ApplyPronunciation(text);

        // Apply smart pauses
        text = ApplySmartPauses(text);

        // Check cache
        var cacheKey = $"{text}_{options?.Voice ?? _config.SelectedVoice}_{options?.Rate ?? 0}";
        if (_config.CacheEnabled && _cache.TryGetValue(cacheKey, out var cached))
        {
            _logger.LogDebug("[TTS] Cache hit for: {Text}", text[..Math.Min(50, text.Length)]);
            return cached;
        }

        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var voice = options?.Voice ?? _config.SelectedVoice;
            var rate = options?.Rate ?? GetCurrentRate();
            var pitch = options?.Pitch ?? GetCurrentPitch();

            var requestBody = JsonSerializer.Serialize(new
            {
                text,
                voice,
                rate = $"+{rate}%",
                pitch = $"+{pitch}Hz"
            });

            var content = new System.Net.Http.StringContent(requestBody, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("http://127.0.0.1:17004/synthesize", content, ct);

            if (response.IsSuccessStatusCode)
            {
                var audioData = await response.Content.ReadAsByteArrayAsync(ct);

                // Cache the result
                if (_config.CacheEnabled && audioData.Length < 1_000_000) // Max 1MB cache
                {
                    _cache[cacheKey] = audioData;
                    if (_cache.Count > 100) // Limit cache size
                    {
                        var oldestKey = _cache.Keys.First();
                        _cache.TryRemove(oldestKey, out _);
                    }
                }

                sw.Stop();
                _logger.LogInformation("[TTS] Synthesized {Length} bytes in {Ms}ms: {Text}",
                    audioData.Length, sw.ElapsedMilliseconds, text[..Math.Min(30, text.Length)]);

                return audioData;
            }

            _logger.LogWarning("[TTS] Synthesis failed with status {Status}", response.StatusCode);
            return Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[TTS] Synthesis failed");
            return Array.Empty<byte>();
        }
    }

    public async Task<IReadOnlyList<VoicePreset>> GetVoicesAsync()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var response = await client.GetAsync("http://127.0.0.1:17004/voices");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<VoicePreset>>(json) ?? GetDefaultVoices();
            }
        }
        catch { }

        return GetDefaultVoices();
    }

    public void SetEmotion(string emotion)
    {
        _config.CurrentEmotion = emotion;
        SaveConfig(_config);
    }

    public void SetSpeed(float speed)
    {
        _config.Speed = Math.Clamp(speed, 0.5f, 2.0f);
        SaveConfig(_config);
    }

    public void SetVolume(float volume)
    {
        _config.Volume = Math.Clamp(volume, 0f, 1f);
        SaveConfig(_config);
    }

    public TtsConfig GetConfig() => _config;

    public void SaveConfig(TtsConfig config)
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
    }

    public async Task<byte[]> SynthesizeWithEffectsAsync(string text, VoiceEffect effect, CancellationToken ct = default)
    {
        var options = effect switch
        {
            VoiceEffect.Whisper => new TtsOptions { Rate = -15, Pitch = -8 },
            VoiceEffect.Excited => new TtsOptions { Rate = 15, Pitch = 8 },
            VoiceEffect.Calm => new TtsOptions { Rate = -5, Pitch = -2 },
            VoiceEffect.Urgent => new TtsOptions { Rate = 20, Pitch = 5 },
            _ => new TtsOptions()
        };

        return await SynthesizeAsync(text, options, ct);
    }

    public IReadOnlyList<VoicePreset> GetDefaultVoices()
    {
        return new List<VoicePreset>
        {
            new() { Name = "fr-FR-HenriNeural", Gender = "Male", Locale = "fr-FR", IsDefault = true },
            new() { Name = "fr-FR-DeniseNeural", Gender = "Female", Locale = "fr-FR" },
            new() { Name = "fr-FR-EloiseNeural", Gender = "Female", Locale = "fr-FR" },
            new() { Name = "fr-FR-JacquesNeural", Gender = "Male", Locale = "fr-FR" },
            new() { Name = "fr-FR-YvetteNeural", Gender = "Female", Locale = "fr-FR" },
            new() { Name = "en-US-GuyNeural", Gender = "Male", Locale = "en-US" },
            new() { Name = "en-US-JennyNeural", Gender = "Female", Locale = "en-US" },
        };
    }

    private string ApplyPronunciation(string text)
    {
        foreach (var (word, pronunciation) in PronunciationOverrides)
        {
            text = text.Replace(word, pronunciation, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private string ApplySmartPauses(string text)
    {
        // Add SSML-like pauses for better prosody
        text = text.Replace(".", "…");
        text = text.Replace(",", "…");
        text = text.Replace(";", "…");
        text = text.Replace("!", "…!");
        text = text.Replace("?", "…?");
        return text;
    }

    private float GetCurrentRate()
    {
        float baseRate = (_config.Speed - 1f) * 100;
        if (EmotionProfiles.TryGetValue(_config.CurrentEmotion, out var emotion))
            baseRate += emotion.rate;
        return Math.Clamp(baseRate, -50, 50);
    }

    private float GetCurrentPitch()
    {
        float basePitch = 0;
        if (EmotionProfiles.TryGetValue(_config.CurrentEmotion, out var emotion))
            basePitch = emotion.pitch;
        return Math.Clamp(basePitch, -20, 20);
    }

    private TtsConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<TtsConfig>(json) ?? new TtsConfig();
            }
        }
        catch { }

        return new TtsConfig
        {
            SelectedVoice = "fr-FR-HenriNeural",
            Speed = 1.0f,
            Volume = 0.8f,
            CurrentEmotion = "neutral",
            CacheEnabled = true,
            SmartPauses = true,
            PronunciationCorrection = true,
            StreamingEnabled = true,
            MaxCacheSize = 100,
            FallbackVoice = "fr-FR-HenriNeural"
        };
    }

    public async ValueTask DisposeAsync()
    {
        _synthLock.Dispose();
        await ValueTask.CompletedTask;
    }
}

public sealed class TtsConfig
{
    public string SelectedVoice { get; set; } = "fr-FR-HenriNeural";
    public float Speed { get; set; } = 1.0f;
    public float Volume { get; set; } = 0.8f;
    public string CurrentEmotion { get; set; } = "neutral";
    public bool CacheEnabled { get; set; } = true;
    public bool SmartPauses { get; set; } = true;
    public bool PronunciationCorrection { get; set; } = true;
    public bool StreamingEnabled { get; set; } = true;
    public int MaxCacheSize { get; set; } = 100;
    public string FallbackVoice { get; set; } = "fr-FR-HenriNeural";
}

public sealed class TtsOptions
{
    public string? Voice { get; set; }
    public float? Rate { get; set; }
    public float? Pitch { get; set; }
    public string? Emotion { get; set; }
}

public sealed class VoicePreset
{
    public string Name { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Locale { get; set; } = "";
    public bool IsDefault { get; set; }
}

public enum VoiceEffect
{
    Neutral,
    Whisper,
    Excited,
    Calm,
    Urgent,
    Happy,
    Sad,
    Serious,
    Empathetic
}
