using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoicePerformanceService
{
    Task PreloadModelsAsync(CancellationToken ct = default);
    void EnableCache(bool enabled);
    void ClearCache();
    VoiceCacheStats GetCacheStats();
    Task OptimizeForLowLatencyAsync();
    Task OptimizeForQualityAsync();
    VoicePerformanceConfig GetConfig();
    void SaveConfig(VoicePerformanceConfig config);
    Task<WarmupResult> WarmupAsync(CancellationToken ct = default);
}

public sealed class VoicePerformanceService : IVoicePerformanceService
{
    private readonly ILogger<VoicePerformanceService> _logger;
    private readonly string _configPath;
    private VoicePerformanceConfig _config;
    private readonly ConcurrentDictionary<string, byte[]> _audioCache = new();
    private readonly ConcurrentDictionary<string, string> _textCache = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    public VoicePerformanceService(ILogger<VoicePerformanceService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "config", "voice_performance.json");
        _config = LoadConfig();
    }

    public async Task PreloadModelsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[VoicePerf] Preloading models...");

        // Preload STT model
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            await client.GetAsync("http://127.0.0.1:17001/warmup", ct);
            _logger.LogInformation("[VoicePerf] STT model preloaded");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VoicePerf] STT preload failed");
        }

        // Preload TTS
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            await client.GetAsync("http://127.0.0.1:17004/health", ct);
            _logger.LogInformation("[VoicePerf] TTS ready");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VoicePerf] TTS check failed");
        }
    }

    public void EnableCache(bool enabled)
    {
        _config.CacheEnabled = enabled;
        SaveConfig(_config);
    }

    public void ClearCache()
    {
        _audioCache.Clear();
        _textCache.Clear();
        _logger.LogInformation("[VoicePerf] Cache cleared");
    }

    public VoiceCacheStats GetCacheStats()
    {
        return new VoiceCacheStats
        {
            AudioEntries = _audioCache.Count,
            TextEntries = _textCache.Count,
            AudioSizeBytes = _audioCache.Values.Sum(v => v.Length),
            HitRate = _config.CacheHits / Math.Max(1, _config.CacheHits + _config.CacheMisses),
            TotalHits = _config.CacheHits,
            TotalMisses = _config.CacheMisses
        };
    }

    public Task OptimizeForLowLatencyAsync()
    {
        _config.Mode = "low_latency";
        _config.CacheEnabled = true;
        _config.PreloadEnabled = true;
        _config.StreamingEnabled = true;
        _config.BatchSize = 1;
        SaveConfig(_config);
        _logger.LogInformation("[VoicePerf] Optimized for low latency");
        return Task.CompletedTask;
    }

    public Task OptimizeForQualityAsync()
    {
        _config.Mode = "high_quality";
        _config.CacheEnabled = true;
        _config.PreloadEnabled = true;
        _config.StreamingEnabled = false;
        _config.BatchSize = 4;
        SaveConfig(_config);
        _logger.LogInformation("[VoicePerf] Optimized for quality");
        return Task.CompletedTask;
    }

    public VoicePerformanceConfig GetConfig() => _config;

    public void SaveConfig(VoicePerformanceConfig config)
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

    public async Task<WarmupResult> WarmupAsync(CancellationToken ct = default)
    {
        var result = new WarmupResult();
        var sw = Stopwatch.StartNew();

        // Warm up STT
        try
        {
            var sttSw = Stopwatch.StartNew();
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync("http://127.0.0.1:17001/health", ct);
            sttSw.Stop();
            result.SttWarmupMs = sttSw.ElapsedMilliseconds;
            result.SttReady = response.IsSuccessStatusCode;
        }
        catch
        {
            result.SttReady = false;
        }

        // Warm up TTS
        try
        {
            var ttsSw = Stopwatch.StartNew();
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync("http://127.0.0.1:17004/health", ct);
            ttsSw.Stop();
            result.TtsWarmupMs = ttsSw.ElapsedMilliseconds;
            result.TtsReady = response.IsSuccessStatusCode;
        }
        catch
        {
            result.TtsReady = false;
        }

        // Warm up Wake Word
        try
        {
            var wakeSw = Stopwatch.StartNew();
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync("http://127.0.0.1:17002/health", ct);
            wakeSw.Stop();
            result.WakeWordWarmupMs = wakeSw.ElapsedMilliseconds;
            result.WakeWordReady = response.IsSuccessStatusCode;
        }
        catch
        {
            result.WakeWordReady = false;
        }

        sw.Stop();
        result.TotalWarmupMs = sw.ElapsedMilliseconds;
        result.Success = result.SttReady || result.TtsReady;

        _logger.LogInformation("[VoicePerf] Warmup completed in {Ms}ms (STT: {Stt}, TTS: {Tts}, Wake: {Wake})",
            result.TotalWarmupMs, result.SttReady, result.TtsReady, result.WakeWordReady);

        return result;
    }

    public string GetCachedAudio(string key)
    {
        if (_config.CacheEnabled && _audioCache.TryGetValue(key, out var cached))
        {
            _config.CacheHits++;
            return Convert.ToBase64String(cached);
        }
        _config.CacheMisses++;
        return "";
    }

    public void CacheAudio(string key, byte[] audio)
    {
        if (_config.CacheEnabled && audio.Length < _config.MaxCacheItemSizeBytes)
        {
            _audioCache[key] = audio;
            if (_audioCache.Count > _config.MaxCacheItems)
            {
                var oldest = _audioCache.OrderBy(x => x.Key).First();
                _audioCache.TryRemove(oldest.Key, out _);
            }
        }
    }

    private VoicePerformanceConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<VoicePerformanceConfig>(json) ?? new VoicePerformanceConfig();
            }
        }
        catch { }

        return new VoicePerformanceConfig
        {
            Mode = "balanced",
            CacheEnabled = true,
            PreloadEnabled = true,
            StreamingEnabled = true,
            BatchSize = 2,
            MaxCacheItems = 100,
            MaxCacheItemSizeBytes = 1_000_000,
            WarmupOnStartup = true,
            AutoOptimize = true
        };
    }
}

public sealed class VoicePerformanceConfig
{
    public string Mode { get; set; } = "balanced";
    public bool CacheEnabled { get; set; } = true;
    public bool PreloadEnabled { get; set; } = true;
    public bool StreamingEnabled { get; set; } = true;
    public int BatchSize { get; set; } = 2;
    public int MaxCacheItems { get; set; } = 100;
    public long MaxCacheItemSizeBytes { get; set; } = 1_000_000;
    public bool WarmupOnStartup { get; set; } = true;
    public bool AutoOptimize { get; set; } = true;
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
}

public sealed class VoiceCacheStats
{
    public int AudioEntries { get; set; }
    public int TextEntries { get; set; }
    public long AudioSizeBytes { get; set; }
    public double HitRate { get; set; }
    public long TotalHits { get; set; }
    public long TotalMisses { get; set; }
}

public sealed class WarmupResult
{
    public bool Success { get; set; }
    public bool SttReady { get; set; }
    public bool TtsReady { get; set; }
    public bool WakeWordReady { get; set; }
    public long SttWarmupMs { get; set; }
    public long TtsWarmupMs { get; set; }
    public long WakeWordWarmupMs { get; set; }
    public long TotalWarmupMs { get; set; }
}
