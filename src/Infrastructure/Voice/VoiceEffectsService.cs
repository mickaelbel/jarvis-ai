using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceEffectsService
{
    Task<byte[]> ApplyEffectAsync(byte[] audio, VoiceEffectType effect, CancellationToken ct = default);
    Task<byte[]> ApplyVolumeAsync(byte[] audio, float volume);
    Task<byte[]> ApplySpeedAsync(byte[] audio, float speed);
    Task<byte[]> ApplyPitchAsync(byte[] audio, float pitchShift);
    Task<byte[]> ApplyReverbAsync(byte[] audio, float amount);
    Task<byte[]> ApplyEchoAsync(byte[] audio, float delay, float decay);
    Task<byte[]> ApplyFadeAsync(byte[] audio, float fadeInMs, float fadeOutMs);
    Task<byte[]> ApplyNormalizationAsync(byte[] audio);
    Task<byte[]> ApplyNoiseGateAsync(byte[] audio, float threshold);
    IReadOnlyList<VoiceEffectType> GetAvailableEffects();
    string GetEffectDescription(VoiceEffectType effect);
}

public sealed class VoiceEffectsService : IVoiceEffectsService
{
    private readonly ILogger<VoiceEffectsService> _logger;

    public VoiceEffectsService(ILogger<VoiceEffectsService> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]> ApplyEffectAsync(byte[] audio, VoiceEffectType effect, CancellationToken ct = default)
    {
        return effect switch
        {
            VoiceEffectType.Whisper => await ApplyWhisperAsync(audio),
            VoiceEffectType.Echo => await ApplyEchoAsync(audio, 200, 0.4f),
            VoiceEffectType.Robot => await ApplyRobotAsync(audio),
            VoiceEffectType.Deep => await ApplyPitchAsync(audio, -3),
            VoiceEffectType.High => await ApplyPitchAsync(audio, 3),
            VoiceEffectType.Fast => await ApplySpeedAsync(audio, 1.5f),
            VoiceEffectType.Slow => await ApplySpeedAsync(audio, 0.7f),
            VoiceEffectType.Calm => await ApplyCalmEffectAsync(audio),
            VoiceEffectType.Urgent => await ApplyUrgentEffectAsync(audio),
            _ => audio
        };
    }

    public Task<byte[]> ApplyVolumeAsync(byte[] audio, float volume)
    {
        var samples = AudioHelper.BytesToSamples(audio);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp(samples[i] * volume, short.MinValue, short.MaxValue);
        }
        return Task.FromResult(AudioHelper.SamplesToBytes(samples));
    }

    public Task<byte[]> ApplySpeedAsync(byte[] audio, float speed)
    {
        // Simple resampling for speed change
        var samples = AudioHelper.BytesToSamples(audio);
        var outputLength = (int)(samples.Length / speed);
        var output = new short[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            var srcIndex = (int)(i * speed);
            if (srcIndex < samples.Length)
                output[i] = samples[srcIndex];
        }

        return Task.FromResult(AudioHelper.SamplesToBytes(output));
    }

    public Task<byte[]> ApplyPitchAsync(byte[] audio, float pitchShift)
    {
        // Simple pitch shift via resampling
        var factor = (float)Math.Pow(2, pitchShift / 12.0);
        return ApplySpeedAsync(audio, factor);
    }

    public Task<byte[]> ApplyReverbAsync(byte[] audio, float amount)
    {
        var samples = AudioHelper.BytesToSamples(audio);
        var delay = (int)(44100 * 0.05 * amount); // 50ms delay scaled by amount
        var output = new short[samples.Length];

        for (int i = 0; i < samples.Length; i++)
        {
            output[i] = samples[i];
            if (i >= delay)
            {
                output[i] = (short)(samples[i] + samples[i - delay] * amount * 0.5);
            }
        }

        return Task.FromResult(AudioHelper.SamplesToBytes(output));
    }

    public Task<byte[]> ApplyEchoAsync(byte[] audio, float delayMs, float decay)
    {
        var samples = AudioHelper.BytesToSamples(audio);
        var delay = (int)(16000 * delayMs / 1000); // 16kHz sample rate
        var output = new short[samples.Length];

        for (int i = 0; i < samples.Length; i++)
        {
            output[i] = samples[i];
            if (i >= delay)
            {
                output[i] = (short)(samples[i] + samples[i - delay] * decay);
            }
        }

        return Task.FromResult(AudioHelper.SamplesToBytes(output));
    }

    public Task<byte[]> ApplyFadeAsync(byte[] audio, float fadeInMs, float fadeOutMs)
    {
        var samples = AudioHelper.BytesToSamples(audio);
        var fadeInSamples = (int)(16000 * fadeInMs / 1000);
        var fadeOutSamples = (int)(16000 * fadeOutMs / 1000);

        for (int i = 0; i < Math.Min(fadeInSamples, samples.Length); i++)
        {
            float factor = (float)i / fadeInSamples;
            samples[i] = (short)(samples[i] * factor);
        }

        for (int i = 0; i < Math.Min(fadeOutSamples, samples.Length); i++)
        {
            int idx = samples.Length - 1 - i;
            float factor = (float)i / fadeOutSamples;
            samples[idx] = (short)(samples[idx] * factor);
        }

        return Task.FromResult(AudioHelper.SamplesToBytes(samples));
    }

    public Task<byte[]> ApplyNormalizationAsync(byte[] audio)
    {
        var samples = AudioHelper.BytesToSamples(audio);
        short max = 0;
        foreach (var s in samples)
        {
            short abs = (short)Math.Abs(s);
            if (abs > max) max = abs;
        }

        if (max == 0) return Task.FromResult(audio);

        float gain = 32767f / max;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(samples[i] * gain);
        }

        return Task.FromResult(AudioHelper.SamplesToBytes(samples));
    }

    public Task<byte[]> ApplyNoiseGateAsync(byte[] audio, float threshold)
    {
        var samples = AudioHelper.BytesToSamples(audio);
        for (int i = 0; i < samples.Length; i++)
        {
            if (Math.Abs(samples[i]) < threshold * 32768)
            {
                samples[i] = 0;
            }
        }
        return Task.FromResult(AudioHelper.SamplesToBytes(samples));
    }

    public IReadOnlyList<VoiceEffectType> GetAvailableEffects()
    {
        return Enum.GetValues<VoiceEffectType>().ToList();
    }

    public string GetEffectDescription(VoiceEffectType effect)
    {
        return effect switch
        {
            VoiceEffectType.Whisper => "Chuchotement doux",
            VoiceEffectType.Echo => "Écho spatial",
            VoiceEffectType.Robot => "Voix robotique",
            VoiceEffectType.Deep => "Voix profonde",
            VoiceEffectType.High => "Voix aiguë",
            VoiceEffectType.Fast => "Parole rapide",
            VoiceEffectType.Slow => "Parole lente",
            VoiceEffectType.Calm => "Ton calme",
            VoiceEffectType.Urgent => "Ton urgent",
            _ => "Aucun effet"
        };
    }

    private Task<byte[]> ApplyWhisperAsync(byte[] audio)
    {
        var samples = AudioHelper.BytesToSamples(audio);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(samples[i] * 0.3); // Reduce volume
        }
        return Task.FromResult(AudioHelper.SamplesToBytes(samples));
    }

    private Task<byte[]> ApplyRobotAsync(byte[] audio)
    {
        var samples = AudioHelper.BytesToSamples(audio);
        var output = new short[samples.Length];
        var lfo = 0.0;

        for (int i = 0; i < samples.Length; i++)
        {
            lfo += 0.1;
            float modulator = (float)Math.Sin(lfo);
            output[i] = (short)(samples[i] * modulator);
        }

        return Task.FromResult(AudioHelper.SamplesToBytes(output));
    }

    private Task<byte[]> ApplyCalmEffectAsync(byte[] audio)
    {
        var result = ApplySpeedAsync(audio, 0.9f).Result;
        return ApplyVolumeAsync(result, 0.85f);
    }

    private Task<byte[]> ApplyUrgentEffectAsync(byte[] audio)
    {
        var result = ApplySpeedAsync(audio, 1.2f).Result;
        return ApplyVolumeAsync(result, 1.1f);
    }
}

public enum VoiceEffectType
{
    None,
    Whisper,
    Echo,
    Robot,
    Deep,
    High,
    Fast,
    Slow,
    Calm,
    Urgent
}

internal static class AudioHelper
{
    public static short[] BytesToSamples(byte[] audio)
    {
        var samples = new short[audio.Length / 2];
        Buffer.BlockCopy(audio, 0, samples, 0, audio.Length);
        return samples;
    }

    public static byte[] SamplesToBytes(short[] samples)
    {
        var audio = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, audio, 0, audio.Length);
        return audio;
    }
}
