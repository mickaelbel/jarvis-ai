namespace JarvisAI.Application.Voice;

public interface ITextToSpeechService
{
    string Name { get; }
    IReadOnlyList<string> AvailableVoices { get; }
    Task<byte[]> SynthesizeWavAsync(string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default);
}
