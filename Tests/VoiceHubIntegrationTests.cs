using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class VoiceHubIntegrationTests
{
    // Jarvis (bureau WPF) choisit son port au démarrage dans [51844, 51860[.
    private static string? _baseUrl;

    private static string BaseUrl => _baseUrl ??= FindJarvisUrl() ?? "";

    private static bool IsWebAppUp()
    {
        // Le port peut être occupé par une autre application locale
        // (tracker tiers, etc.) : on exige une réponse typique de Jarvis.
        return !string.IsNullOrEmpty(BaseUrl);
    }

    /// <summary>
    /// Ces tests live exigent le pipeline audio réel (micro BT, serveurs voix).
    /// Opt-in via JARVIS_LIVE_AUDIO_TESTS=1, ou saut propre si le moteur est
    /// à l'arrêt (casque déconnecté…).
    /// </summary>
    private static bool IsVoiceEngineActive()
    {
        if (Environment.GetEnvironmentVariable("JARVIS_LIVE_AUDIO_TESTS") != "1")
            return false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var body = client.GetStringAsync(BaseUrl + "/api/app-voice/status").GetAwaiter().GetResult();
            return body.Contains("\"engineActive\":true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string? FindJarvisUrl()
    {
        for (var port = 51844; port < 51860; port++)
        {
            var url = $"http://127.0.0.1:{port}";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var res = client.GetAsync(url + "/api/voice/voices").GetAwaiter().GetResult();
                if (!res.IsSuccessStatusCode) continue;
                var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.TrimStart().StartsWith("[") && body.Contains("\"engine\"", StringComparison.OrdinalIgnoreCase))
                    return url;
            }
            catch
            {
                // port libre ou pas Jarvis : on continue
            }
        }
        return null;
    }

    private static async Task<byte[]> GetSpeechWavAsync(HttpClient http, string text, string engine = "piper", string voice = "fr_FR-upmc-medium")
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            text,
            voice,
            engine,
            volume = 1.0
        });
        var res = await http.PostAsync("/api/voice/test",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync();
    }

    private static byte[] WavToPcm16(byte[] wav)
    {
        if (wav.Length < 44) return wav;
        return wav[44..];
    }

    private static int ReadWavSampleRate(byte[] wav)
    {
        if (wav.Length < 28) return 22050;
        return BitConverter.ToInt32(wav, 24);
    }

    private static byte[] ResamplePcm16(byte[] pcm16, int fromRate, int toRate)
    {
        if (fromRate == toRate || pcm16.Length < 2) return pcm16;
        var samples = new short[pcm16.Length / 2];
        Buffer.BlockCopy(pcm16, 0, samples, 0, pcm16.Length);
        var newLen = (int)((long)samples.Length * toRate / fromRate);
        var result = new short[newLen];
        for (var i = 0; i < newLen; i++)
        {
            var pos = (double)i * fromRate / toRate;
            var idx = (int)pos;
            if (idx >= samples.Length - 1)
            {
                result[i] = samples[samples.Length - 1];
                continue;
            }
            var frac = pos - idx;
            result[i] = (short)(samples[idx] + (samples[idx + 1] - samples[idx]) * frac);
        }
        var bytes = new byte[newLen * 2];
        Buffer.BlockCopy(result, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static async Task SendPcmAsync(HubConnection connection, byte[] pcm)
    {
        // Le sampleRate est obligatoire : SignalR n'honore pas les paramètres
        // optionnels C# du hub (Â« target expects 2 Â» sinon). On envoie du 16 kHz.
        const int chunkSize = 4096 * 2;
        for (var i = 0; i < pcm.Length; i += chunkSize)
        {
            var chunk = pcm.AsSpan(i, Math.Min(chunkSize, pcm.Length - i)).ToArray();
            await connection.InvokeAsync("SendAudioChunk", Convert.ToBase64String(chunk), 16000);
        }
        await connection.InvokeAsync("EndUtterance");
    }

    [Fact]
    public async Task Wake_word_utterance_triggers_prompt_response()
    {
        if (!IsWebAppUp()) return;
        if (!IsVoiceEngineActive()) return;

        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        await using var connection = new HubConnectionBuilder()
            .WithUrl(BaseUrl + "/hubs/voice")
            .Build();

        var transcripts = new List<string>();
        var audioEvents = new List<string>();
        var statuses = new List<string>();

        connection.On<string>("voiceTranscript", transcripts.Add);
        connection.On<string>("voiceAudio", audioEvents.Add);
        connection.On<string>("voiceStatus", statuses.Add);

        await connection.StartAsync();

        var wav = await GetSpeechWavAsync(http, "Jarvis", "windows", "Microsoft Hortense Desktop");
        var rate = ReadWavSampleRate(wav);
        await SendPcmAsync(connection, ResamplePcm16(WavToPcm16(wav), rate, 16000));

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (transcripts.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        await connection.DisposeAsync();

        Assert.NotEmpty(transcripts);
        Assert.Contains(transcripts, t => t.Contains("jarvis", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Full_voice_conversation_produces_tts_response()
    {
        if (!IsWebAppUp()) return;
        if (!IsVoiceEngineActive()) return;

        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        await using var connection = new HubConnectionBuilder()
            .WithUrl(BaseUrl + "/hubs/voice")
            .Build();

        var transcripts = new List<string>();
        var audioEvents = new List<string>();

        connection.On<string>("voiceTranscript", transcripts.Add);
        connection.On<string>("voiceAudio", audioEvents.Add);

        await connection.StartAsync();

        var wakeWav = await GetSpeechWavAsync(http, "Jarvis", "windows", "Microsoft Hortense Desktop");
        var wakeRate = ReadWavSampleRate(wakeWav);
        await SendPcmAsync(connection, ResamplePcm16(WavToPcm16(wakeWav), wakeRate, 16000));

        var wakeDeadline = DateTime.UtcNow.AddSeconds(60);
        while (transcripts.Count < 1 && DateTime.UtcNow < wakeDeadline)
        {
            await Task.Delay(500);
        }
        Assert.NotEmpty(transcripts);

        var commandWav = await GetSpeechWavAsync(http, "Quelle heure est-il", "windows", "Microsoft Hortense Desktop");
        var commandRate = ReadWavSampleRate(commandWav);
        await SendPcmAsync(connection, ResamplePcm16(WavToPcm16(commandWav), commandRate, 16000));

        var commandDeadline = DateTime.UtcNow.AddSeconds(120);
        while (transcripts.Count < 2 && DateTime.UtcNow < commandDeadline)
        {
            await Task.Delay(500);
        }
        Assert.True(transcripts.Count >= 2, "Le transcript de la commande n'est jamais arrivé");
        Assert.Contains("heure", transcripts[^1], StringComparison.OrdinalIgnoreCase);

        var audioDeadline = DateTime.UtcNow.AddSeconds(120);
        while (audioEvents.Count == 0 && DateTime.UtcNow < audioDeadline)
        {
            await Task.Delay(500);
        }
        Assert.NotEmpty(audioEvents);

        await connection.DisposeAsync();
    }
}
