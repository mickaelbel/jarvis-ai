using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Speech.Synthesis;

namespace JarvisAI.Infrastructure.Voice;

public sealed class WindowsSpeechTextToSpeechService : ITextToSpeechService
{
    private readonly ILogger<WindowsSpeechTextToSpeechService> _logger;
    private IReadOnlyList<string>? _cachedVoices;

    public string Name => "Voix Windows";

    public WindowsSpeechTextToSpeechService(ILogger<WindowsSpeechTextToSpeechService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> AvailableVoices
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return Array.Empty<string>();

            if (_cachedVoices is not null) return _cachedVoices;

            try
            {
                var voices = new List<string>();
                using var synth = new SpeechSynthesizer();
                foreach (var installed in synth.GetInstalledVoices())
                {
                    if (installed.Enabled && installed.VoiceInfo is not null)
                        voices.Add(installed.VoiceInfo.Name);
                }
                _cachedVoices = voices;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SAPI] Failed to enumerate installed voices");
                _cachedVoices = Array.Empty<string>();
            }

            return _cachedVoices;
        }
    }

    public Task<byte[]> SynthesizeWavAsync(
        string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required", nameof(text));

        text = TtsPronunciation.Normalize(text, voice);

        return Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows Speech (SAPI) is only available on Windows.");
            return SynthesizeWavCore(text, voice, volume, speed);
        }, cancellationToken);
    }

    private byte[] SynthesizeWavCore(string text, string voice, float volume, float speed)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Speech (SAPI) is only available on Windows.");

        using var speech = new SpeechSynthesizer();
        speech.Volume = (int)Math.Clamp(volume * 100f, 0f, 100f);

        // SAPI : rate = 0 (normal), -10 à +10. Map vitesse (0.5-2.0) → rate.
        var rate = (int)Math.Round(Math.Clamp((speed - 1.0f) * 10f, -10f, 10f));
        if (rate != 0) speech.Rate = rate;

        if (!string.IsNullOrWhiteSpace(voice))
        {
            try
            {
                var installed = new List<VoiceInfo>();
                foreach (var v in speech.GetInstalledVoices())
                {
                    if (v.VoiceInfo is not null)
                        installed.Add(v.VoiceInfo);
                }

                VoiceInfo? match = null;
                foreach (var v in installed)
                {
                    if (string.Equals(v.Name, voice, StringComparison.OrdinalIgnoreCase))
                    {
                        match = v;
                        break;
                    }
                }

                if (match is null)
                {
                    var lang = voice.Contains("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
                    foreach (var v in installed)
                    {
                        if (v.Culture.Name.StartsWith(lang, StringComparison.OrdinalIgnoreCase))
                        {
                            match = v;
                            break;
                        }
                    }
                }

                if (match is not null)
                    speech.SelectVoice(match.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SAPI] Failed to select voice '{Voice}'", voice);
            }
        }

        using var ms = new MemoryStream();
        speech.SetOutputToWaveStream(ms);
        speech.Speak(text);
        var wav = ms.ToArray();
        return wav.Length > 0 ? wav : throw new InvalidOperationException("SAPI produced no audio");
    }
}
