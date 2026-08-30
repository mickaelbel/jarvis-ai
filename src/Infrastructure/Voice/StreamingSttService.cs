using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public sealed class StreamingSttService : IStreamingSttService
{
    private readonly ILogger<StreamingSttService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _sttUrl;

    public StreamingSttService(ILogger<StreamingSttService> logger, HttpClient httpClient, string sttUrl = "http://127.0.0.1:17003")
    {
        _logger = logger;
        _httpClient = httpClient;
        _sttUrl = sttUrl;
    }

    public async IAsyncEnumerable<SttPartialResult> StreamTranscribeAsync(
        IAsyncEnumerable<byte[]> audioChunks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new List<byte>();
        var chunkIndex = 0;

        await foreach (var chunk in audioChunks.WithCancellation(ct))
        {
            buffer.AddRange(chunk);
            chunkIndex++;

            if (chunkIndex % 5 == 0 && buffer.Count > 0)
            {
                var audioData = buffer.ToArray();
                var result = await TranscribePartialAsync(audioData, ct);

                if (result is not null)
                {
                    yield return result;

                    if (result.IsFinal)
                        buffer.Clear();
                }
            }
        }

        if (buffer.Count > 0)
        {
            var finalResult = await TranscribePartialAsync(buffer.ToArray(), ct);
            if (finalResult is not null)
                yield return finalResult;
        }
    }

    public async Task<SttResult> TranscribeAsync(byte[] audio, CancellationToken ct = default)
    {
        try
        {
            var content = new MultipartFormDataContent
            {
                { new ByteArrayContent(audio), "audio", "audio.wav" }
            };

            var response = await _httpClient.PostAsync($"{_sttUrl}/transcribe", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<SttResponse>(json);

            return new SttResult(
                true,
                result?.Text ?? "",
                result?.Language ?? "fr",
                result?.Duration ?? 0,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StreamingSTT] Transcribe failed");
            return new SttResult(false, "", "fr", 0, ex.Message);
        }
    }

    private async Task<SttPartialResult?> TranscribePartialAsync(byte[] audio, CancellationToken ct)
    {
        try
        {
            var content = new MultipartFormDataContent
            {
                { new ByteArrayContent(audio), "audio", "partial.wav" }
            };

            var response = await _httpClient.PostAsync($"{_sttUrl}/transcribe_partial", content, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<SttPartialResponse>(json);

            if (result is null || string.IsNullOrEmpty(result.Text))
                return null;

            return new SttPartialResult(
                result.Text,
                result.IsFinal,
                result.Confidence,
                TimeSpan.FromSeconds(result.Duration));
        }
        catch
        {
            return null;
        }
    }

    private sealed record SttResponse(string? Text, double Confidence, double Duration, string? Language);
    private sealed record SttPartialResponse(string? Text, bool IsFinal, double Confidence, double Duration);
}
