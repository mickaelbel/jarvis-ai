namespace JarvisAI.Application.Voice;

public sealed record WakeWordDetection(bool Triggered, double Score, double Clicks, int ElapsedMs);

public interface IWakeWordDetector
{
    string Name { get; }
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<bool> EnsureStartedAsync(CancellationToken cancellationToken = default);
    Task<WakeWordDetection> DetectAsync(byte[] pcm16, int sampleRate, CancellationToken cancellationToken = default);
}
