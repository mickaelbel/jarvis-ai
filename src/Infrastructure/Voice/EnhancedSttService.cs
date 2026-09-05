using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IEnhancedSttService
{
    Task<EnhancedSttResult> TranscribeAsync(byte[] audioData, int sampleRate = 16000, CancellationToken ct = default);
    IAsyncEnumerable<SttStreamChunk> TranscribeStreamAsync(IAsyncEnumerable<byte[]> audioStream, CancellationToken ct = default);
    void CalibrateMicrophone();
    void SetNoiseReductionLevel(float level);
    SttConfig GetConfig();
    void SaveConfig(SttConfig config);
    SttDiagnostics GetDiagnostics();
    Task<List<LanguageOption>> GetSupportedLanguagesAsync();
}

public sealed class EnhancedSttService : IEnhancedSttService, IAsyncDisposable
{
    private readonly ILogger<EnhancedSttService> _logger;
    private readonly string _configPath;
    private SttConfig _config;
    private readonly SttDiagnostics _diagnostics = new();
    private readonly ConcurrentQueue<float> _noiseFloorSamples = new();
    private float _noiseFloor = 0.01f;
    private readonly SemaphoreSlim _transcribeLock = new(1, 1);

    public EnhancedSttService(ILogger<EnhancedSttService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "config", "stt.json");
        _config = LoadConfig();
    }

    public async Task<EnhancedSttResult> TranscribeAsync(byte[] audioData, int sampleRate = 16000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _diagnostics.TotalRequests++;

        try
        {
            // Apply noise reduction if enabled
            if (_config.NoiseReductionEnabled)
            {
                audioData = ApplyNoiseReduction(audioData, sampleRate);
            }

            // Apply adaptive VAD threshold
            if (_config.AdaptiveVad)
            {
                var vadThreshold = CalculateVadThreshold(audioData, sampleRate);
                if (vadThreshold < _config.VadThreshold)
                {
                    _logger.LogDebug("[STT] Audio below VAD threshold, skipping");
                    return new EnhancedSttResult { Text = "", Confidence = 0, Language = _config.Language };
                }
            }

            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var content = new System.Net.Http.ByteArrayContent(audioData);
            content.Headers.Add("X-Sample-Rate", sampleRate.ToString());
            content.Headers.Add("X-Language", _config.Language);
            content.Headers.Add("X-Model", _config.Model);

            var response = await client.PostAsync("http://127.0.0.1:17001/transcribe", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            var result = JsonSerializer.Deserialize<SttResponseDto>(json);
            if (result is null)
                return new EnhancedSttResult { Text = "", Confidence = 0 };

            sw.Stop();
            _diagnostics.SuccessfulRequests++;
            _diagnostics.AverageLatencyMs = (_diagnostics.AverageLatencyMs * (_diagnostics.SuccessfulRequests - 1) + sw.ElapsedMilliseconds) / _diagnostics.SuccessfulRequests;

            return new EnhancedSttResult
            {
                Text = result.text ?? "",
                Confidence = result.confidence,
                Language = result.language ?? _config.Language,
                Duration = sw.Elapsed,
                Segments = result.segments?.Select(s => new SttSegment
                {
                    Text = s.text ?? "",
                    StartMs = (long)(s.start * 1000),
                    EndMs = (long)(s.end * 1000)
                }).ToList() ?? new List<SttSegment>()
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _diagnostics.FailedRequests++;
            _logger.LogError(ex, "[STT] Transcription failed");
            return new EnhancedSttResult { Text = "", Confidence = 0, Error = ex.Message };
        }
    }

    public async IAsyncEnumerable<SttStreamChunk> TranscribeStreamAsync(IAsyncEnumerable<byte[]> audioStream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new List<byte>();
        var chunkSize = _config.StreamingChunkMs * 16 * 2; // 16kHz mono 16-bit

        await foreach (var chunk in audioStream.WithCancellation(ct))
        {
            buffer.AddRange(chunk);

            if (buffer.Count >= chunkSize)
            {
                var audioData = buffer.ToArray();
                buffer.Clear();

                var result = await TranscribeAsync(audioData, 16000, ct);
                if (!string.IsNullOrEmpty(result.Text))
                {
                    yield return new SttStreamChunk
                    {
                        Text = result.Text,
                        IsPartial = false,
                        Timestamp = DateTime.UtcNow
                    };
                }
            }
        }

        // Process remaining buffer
        if (buffer.Count > 0)
        {
            var result = await TranscribeAsync(buffer.ToArray(), 16000, ct);
            if (!string.IsNullOrEmpty(result.Text))
            {
                yield return new SttStreamChunk
                {
                    Text = result.Text,
                    IsPartial = false,
                    Timestamp = DateTime.UtcNow
                };
            }
        }
    }

    public void CalibrateMicrophone()
    {
        _logger.LogInformation("[STT] Calibrating microphone noise floor...");
        _noiseFloorSamples.Clear();

        // Sample noise floor from recent audio
        Task.Run(async () =>
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);

                // Record 1 second of ambient noise
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-f dshow -i audio=\"Microphone\" -t 1 -ar 16000 -ac 1 -f s16le pipe:1",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process is not null)
                {
                    var buffer = new byte[32000];
                    var bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer);
                    var samples = new short[bytesRead / 2];
                    Buffer.BlockCopy(buffer, 0, samples, 0, bytesRead);

                    float sum = 0;
                    foreach (var sample in samples)
                    {
                        sum += Math.Abs(sample / 32768f);
                    }
                    _noiseFloor = sum / samples.Length * 2;

                    _logger.LogInformation("[STT] Noise floor calibrated: {Floor:F4}", _noiseFloor);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[STT] Calibration failed, using default noise floor");
            }
        });
    }

    public void SetNoiseReductionLevel(float level)
    {
        _config.NoiseReductionLevel = Math.Clamp(level, 0, 1);
        SaveConfig(_config);
    }

    public SttConfig GetConfig() => _config;

    public void SaveConfig(SttConfig config)
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

    public SttDiagnostics GetDiagnostics() => _diagnostics;

    public Task<List<LanguageOption>> GetSupportedLanguagesAsync()
    {
        return Task.FromResult(new List<LanguageOption>
        {
            new() { Code = "fr", Name = "Français", Accuracy = 0.95f },
            new() { Code = "en", Name = "English", Accuracy = 0.93f },
            new() { Code = "de", Name = "Deutsch", Accuracy = 0.90f },
            new() { Code = "es", Name = "Español", Accuracy = 0.91f },
            new() { Code = "it", Name = "Italiano", Accuracy = 0.89f },
            new() { Code = "pt", Name = "Português", Accuracy = 0.88f },
            new() { Code = "nl", Name = "Nederlands", Accuracy = 0.87f },
            new() { Code = "ja", Name = "日本語", Accuracy = 0.85f },
            new() { Code = "zh", Name = "中文", Accuracy = 0.84f },
            new() { Code = "auto", Name = "Auto-detect", Accuracy = 0.90f },
        });
    }

    private byte[] ApplyNoiseReduction(byte[] audio, int sampleRate)
    {
        if (_config.NoiseReductionLevel <= 0) return audio;

        var samples = new short[audio.Length / 2];
        Buffer.BlockCopy(audio, 0, samples, 0, audio.Length);

        float reductionFactor = _config.NoiseReductionLevel * 0.5f;

        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i] / 32768f;
            float noise = _noiseFloor * reductionFactor;

            if (Math.Abs(sample) < noise)
            {
                samples[i] = 0;
            }
            else
            {
                // Apply soft knee reduction
                float sign = sample > 0 ? 1 : -1;
                float amplitude = Math.Abs(sample);
                float reduced = Math.Max(0, amplitude - noise);
                samples[i] = (short)(sign * reduced * 32768f);
            }
        }

        var result = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, result, 0, result.Length);
        return result;
    }

    private float CalculateVadThreshold(byte[] audio, int sampleRate)
    {
        var samples = new short[audio.Length / 2];
        Buffer.BlockCopy(audio, 0, samples, 0, audio.Length);

        float energy = 0;
        foreach (var sample in samples)
        {
            float normalized = sample / 32768f;
            energy += normalized * normalized;
        }
        energy /= samples.Length;

        return (float)Math.Sqrt(energy);
    }

    private SttConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<SttConfig>(json) ?? new SttConfig();
            }
        }
        catch { }

        return new SttConfig
        {
            Model = "small",
            Language = "fr",
            NoiseReductionEnabled = true,
            NoiseReductionLevel = 0.3f,
            AdaptiveVad = true,
            VadThreshold = 0.02f,
            StreamingChunkMs = 500,
            MultiLanguage = false,
            ContinuousMode = false,
            WhisperTimeoutMs = 30000
        };
    }

    public async ValueTask DisposeAsync()
    {
        _transcribeLock.Dispose();
        await ValueTask.CompletedTask;
    }
}

public sealed class SttConfig
{
    public string Model { get; set; } = "small";
    public string Language { get; set; } = "fr";
    public bool NoiseReductionEnabled { get; set; } = true;
    public float NoiseReductionLevel { get; set; } = 0.3f;
    public bool AdaptiveVad { get; set; } = true;
    public float VadThreshold { get; set; } = 0.02f;
    public int StreamingChunkMs { get; set; } = 500;
    public bool MultiLanguage { get; set; } = false;
    public bool ContinuousMode { get; set; } = false;
    public int WhisperTimeoutMs { get; set; } = 30000;
    public bool SpeakerDiarization { get; set; } = false;
    public bool WhisperDetection { get; set; } = false;
    public bool PostCorrection { get; set; } = true;
}

public sealed class EnhancedSttResult
{
    public string Text { get; set; } = "";
    public float Confidence { get; set; }
    public string Language { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public List<SttSegment> Segments { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class SttSegment
{
    public string Text { get; set; } = "";
    public long StartMs { get; set; }
    public long EndMs { get; set; }
}

public sealed class SttStreamChunk
{
    public string Text { get; set; } = "";
    public bool IsPartial { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class SttDiagnostics
{
    public long TotalRequests { get; set; }
    public long SuccessfulRequests { get; set; }
    public long FailedRequests { get; set; }
    public double AverageLatencyMs { get; set; }
    public DateTime LastRequestAt { get; set; }
}

public sealed class LanguageOption
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public float Accuracy { get; set; }
}

internal class SttResponseDto
{
    public string? text { get; set; }
    public float confidence { get; set; }
    public string? language { get; set; }
    public List<SttSegmentDto>? segments { get; set; }
}

internal class SttSegmentDto
{
    public string? text { get; set; }
    public float start { get; set; }
    public float end { get; set; }
}
