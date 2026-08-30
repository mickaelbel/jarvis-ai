using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IEnhancedWakeWordService
{
    Task<bool> DetectAsync(byte[] audioData, int sampleRate = 16000, CancellationToken ct = default);
    void SetSensitivity(float sensitivity);
    void AddCustomWakeWord(string word, string? alias = null);
    void RemoveCustomWakeWord(string word);
    IReadOnlyList<WakeWordEntry> GetWakeWords();
    void SetActive(bool active);
    bool IsActive();
    WakeWordConfig GetConfig();
    void SaveConfig(WakeWordConfig config);
    event EventHandler<WakeWordDetectedEventArgs>? OnWakeWordDetected;
}

public sealed class EnhancedWakeWordService : IEnhancedWakeWordService, IAsyncDisposable
{
    private readonly ILogger<EnhancedWakeWordService> _logger;
    private readonly string _configPath;
    private WakeWordConfig _config;
    private bool _isActive = true;
    private readonly ConcurrentDictionary<string, DateTime> _recentDetections = new();
    private readonly SemaphoreSlim _detectLock = new(1, 1);

    public event EventHandler<WakeWordDetectedEventArgs>? OnWakeWordDetected;

    // Default wake words with aliases
    private static readonly Dictionary<string, string[]> DefaultWakeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jarvis"] = new[] { "jarvis", "jervis", "jarvis", "jervis", "hey jarvis", "ok jarvis" },
        ["assistant"] = new[] { "assistant", "assistant" },
        ["ok google"] = new[] { "ok google", "ok google" },
        ["hey siri"] = new[] { "hey siri" },
        ["alexa"] = new[] { "alexa", "alicia" },
    };

    public EnhancedWakeWordService(ILogger<EnhancedWakeWordService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "config", "wakeword.json");
        _config = LoadConfig();
    }

    public async Task<bool> DetectAsync(byte[] audioData, int sampleRate = 16000, CancellationToken ct = default)
    {
        if (!_isActive) return false;

        try
        {
            // Send to wake word server
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(3);

            var content = new System.Net.Http.ByteArrayContent(audioData);
            content.Headers.Add("X-Sample-Rate", sampleRate.ToString());

            var response = await client.PostAsync("http://127.0.0.1:17002/detect", content, ct);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<WakeWordDetectionResult>(json);

                if (result?.detected == true)
                {
                    var detectedWord = result.word ?? "jarvis";

                    // Check debounce
                    if (_recentDetections.TryGetValue(detectedWord, out var lastDetection))
                    {
                        if ((DateTime.UtcNow - lastDetection).TotalSeconds < _config.DebounceSeconds)
                        {
                            return false;
                        }
                    }

                    _recentDetections[detectedWord] = DateTime.UtcNow;

                    _logger.LogInformation("[WakeWord] Detected: {Word} (confidence: {Confidence:F2})",
                        detectedWord, result.confidence);

                    OnWakeWordDetected?.Invoke(this, new WakeWordDetectedEventArgs
                    {
                        Word = detectedWord,
                        Confidence = result.confidence,
                        Timestamp = DateTime.UtcNow
                    });

                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WakeWord] Detection failed");
        }

        return false;
    }

    public void SetSensitivity(float sensitivity)
    {
        _config.Sensitivity = Math.Clamp(sensitivity, 0.1f, 1.0f);
        SaveConfig(_config);
    }

    public void AddCustomWakeWord(string word, string? alias = null)
    {
        if (!_config.CustomWakeWords.ContainsKey(word.ToLowerInvariant()))
        {
            _config.CustomWakeWords[word.ToLowerInvariant()] = alias ?? word;
            SaveConfig(_config);
            _logger.LogInformation("[WakeWord] Added custom word: {Word}", word);
        }
    }

    public void RemoveCustomWakeWord(string word)
    {
        if (_config.CustomWakeWords.Remove(word.ToLowerInvariant()))
        {
            SaveConfig(_config);
            _logger.LogInformation("[WakeWord] Removed custom word: {Word}", word);
        }
    }

    public IReadOnlyList<WakeWordEntry> GetWakeWords()
    {
        var entries = new List<WakeWordEntry>();

        foreach (var (word, aliases) in DefaultWakeWords)
        {
            entries.Add(new WakeWordEntry
            {
                Word = word,
                Aliases = aliases,
                IsCustom = false,
                Enabled = _config.EnabledWords.Contains(word.ToLowerInvariant()) || !_config.EnabledWords.Any()
            });
        }

        foreach (var (word, alias) in _config.CustomWakeWords)
        {
            entries.Add(new WakeWordEntry
            {
                Word = word,
                Aliases = new[] { alias },
                IsCustom = true,
                Enabled = true
            });
        }

        return entries;
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        _logger.LogInformation("[WakeWord] Active: {Active}", active);
    }

    public bool IsActive() => _isActive;

    public WakeWordConfig GetConfig() => _config;

    public void SaveConfig(WakeWordConfig config)
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

    private WakeWordConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<WakeWordConfig>(json) ?? new WakeWordConfig();
            }
        }
        catch { }

        return new WakeWordConfig
        {
            PrimaryWord = "jarvis",
            Sensitivity = 0.5f,
            DebounceSeconds = 3,
            EnabledWords = new List<string> { "jarvis" },
            CustomWakeWords = new Dictionary<string, string>(),
            EnableVoiceConfirmation = true,
            ConfirmationMessage = "Oui ?",
            AutoDeactivateAfterSeconds = 30,
            EnablePresenceDetection = false,
            ModelPath = "hey_jarvis_v0.1.onnx"
        };
    }

    public async ValueTask DisposeAsync()
    {
        _detectLock.Dispose();
        await ValueTask.CompletedTask;
    }
}

public sealed class WakeWordConfig
{
    public string PrimaryWord { get; set; } = "jarvis";
    public float Sensitivity { get; set; } = 0.5f;
    public int DebounceSeconds { get; set; } = 3;
    public List<string> EnabledWords { get; set; } = new();
    public Dictionary<string, string> CustomWakeWords { get; set; } = new();
    public bool EnableVoiceConfirmation { get; set; } = true;
    public string ConfirmationMessage { get; set; } = "Oui ?";
    public int AutoDeactivateAfterSeconds { get; set; } = 30;
    public bool EnablePresenceDetection { get; set; } = false;
    public string ModelPath { get; set; } = "hey_jarvis_v0.1.onnx";
}

public sealed class WakeWordEntry
{
    public string Word { get; set; } = "";
    public string[] Aliases { get; set; } = Array.Empty<string>();
    public bool IsCustom { get; set; }
    public bool Enabled { get; set; }
}

public sealed class WakeWordDetectedEventArgs : EventArgs
{
    public string Word { get; set; } = "";
    public float Confidence { get; set; }
    public DateTime Timestamp { get; set; }
}

internal class WakeWordDetectionResult
{
    public bool detected { get; set; }
    public string? word { get; set; }
    public float confidence { get; set; }
}
