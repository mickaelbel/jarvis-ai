namespace JarvisAI.Application.Voice;

public interface ISpeechToTextService
{
    string Name { get; }
    Task<SttResult> TranscribeAsync(byte[] pcm16, int sampleRate, string? language = null, CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

public sealed record SttResult(bool Success, string Text, string? Language, double ElapsedMs, string? Error)
{
    public static SttResult Ok(string text, string? language, double elapsedMs)
        => new(true, text, language, elapsedMs, null);

    public static SttResult Failed(string error, double elapsedMs = 0)
        => new(false, string.Empty, null, elapsedMs, error);
}
