using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Voice;

public sealed class PiperTextToSpeechService : ITextToSpeechService
{
    private readonly ILogger<PiperTextToSpeechService> _logger;
    private readonly string? _piperDir;
    private readonly string? _piperExe;
    private IReadOnlyList<string>? _cachedVoices;

    public string Name => "Piper";

    public PiperTextToSpeechService(ILogger<PiperTextToSpeechService> logger)
    {
        _logger = logger;
        _piperDir = VoicePaths.FindPiperDirectory();
        _piperExe = _piperDir is null ? null : Path.Combine(_piperDir, "piper.exe");
    }

    public IReadOnlyList<string> AvailableVoices
    {
        get
        {
            if (_cachedVoices is not null) return _cachedVoices;

            var voices = new List<string>();
            if (_piperDir is not null)
            {
                foreach (var dir in new[] { _piperDir, Path.GetDirectoryName(_piperDir)! })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.GetFiles(dir, "*.onnx"))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (!voices.Contains(name)) voices.Add(name);
                    }
                }
            }

            _cachedVoices = voices;
            return voices;
        }
    }

    public async Task<byte[]> SynthesizeWavAsync(
        string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required", nameof(text));

        text = TtsPronunciation.Normalize(text, voice);

        if (_piperExe is null || !File.Exists(_piperExe))
            throw new InvalidOperationException($"Piper executable not found. Expected at: {_piperExe}");

        var voiceModel = ResolveVoiceModel(voice);
        var tempWav = Path.Combine(Path.GetTempPath(), $"jarvis_tts_{Guid.NewGuid():N}.wav");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _piperExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = _piperDir
            };

            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(voiceModel);
            psi.ArgumentList.Add("--output_file");
            psi.ArgumentList.Add(tempWav);

            var lengthScale = Math.Clamp(speed, 0.4f, 2.0f);
            if (lengthScale is < 0.99f or > 1.01f)
            {
                // Piper : length_scale < 1 accélère la voix, > 1 la ralentit.
                psi.ArgumentList.Add("--length_scale");
                psi.ArgumentList.Add(lengthScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            _logger.LogInformation("[Piper] Synthesizing ({Chars} chars, voice={Voice}, speed={Speed})", text.Length, Path.GetFileName(voiceModel), lengthScale);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                throw new InvalidOperationException("Failed to start piper process");

            await process.StandardInput.WriteAsync(text);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var err = (await stderrTask).Trim();
                _logger.LogError("[Piper] Exit code {Code}: {Error}", process.ExitCode, err);
                throw new InvalidOperationException($"Piper failed (exit {process.ExitCode}): {err}");
            }

            if (!File.Exists(tempWav))
                throw new InvalidOperationException("Piper did not produce an output file");

            var wav = await File.ReadAllBytesAsync(tempWav, cancellationToken);
            if (wav.Length == 0)
                throw new InvalidOperationException("Piper produced an empty output file");

            return ApplyVolume(wav, volume);
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
    }

    private string ResolveVoiceModel(string voice)
    {
        if (string.IsNullOrWhiteSpace(voice)) voice = "fr_FR-upmc-medium";

        var candidates = new List<string>();
        var voiceDir = Path.GetDirectoryName(_piperDir);

        if (Path.IsPathRooted(voice) && File.Exists(voice))
            candidates.Add(voice);

        foreach (var baseDir in new[] { _piperDir, voiceDir })
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) continue;
            candidates.Add(Path.Combine(baseDir, voice + ".onnx"));
        }

        foreach (var candidate in candidates.Distinct())
        {
            if (File.Exists(candidate)) return candidate;
        }

        // Fallback: any available voice
        if (AvailableVoices.Count > 0)
        {
            foreach (var baseDir in new[] { _piperDir, voiceDir })
            {
                var fallback = Path.Combine(baseDir!, AvailableVoices[0] + ".onnx");
                if (File.Exists(fallback))
                {
                    _logger.LogWarning("[Piper] Voice '{Voice}' not found, using '{Fallback}'", voice, AvailableVoices[0]);
                    return fallback;
                }
            }
        }

        throw new InvalidOperationException($"No piper voice found for '{voice}'");
    }

    private static byte[] ApplyVolume(byte[] wav, float volume)
    {
        if (volume is >= 1.0f or <= 0f) return wav;

        // WAV: 44-byte header, 16-bit mono
        if (wav.Length <= 44) return wav;

        var result = (byte[])wav.Clone();
        for (var i = 44; i + 1 < result.Length; i += 2)
        {
            var sample = BitConverter.ToInt16(result, i);
            var scaled = (short)Math.Clamp((int)(sample * volume), short.MinValue, short.MaxValue);
            var bytes = BitConverter.GetBytes(scaled);
            result[i] = bytes[0];
            result[i + 1] = bytes[1];
        }
        return result;
    }
}
