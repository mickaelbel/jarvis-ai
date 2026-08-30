namespace JarvisAI.Application.Voice;

/// <summary>
/// STT en streaming avec transcription partielle incrémentale.
/// </summary>
public interface IStreamingSttService
{
    IAsyncEnumerable<SttPartialResult> StreamTranscribeAsync(IAsyncEnumerable<byte[]> audioChunks, CancellationToken ct = default);
    Task<SttResult> TranscribeAsync(byte[] audio, CancellationToken ct = default);
}

public sealed record SttPartialResult(
    string Text,
    bool IsFinal,
    double Confidence,
    TimeSpan Duration);

public sealed record StreamingSttResult(
    string Text,
    double Confidence,
    TimeSpan Duration,
    string Language);
