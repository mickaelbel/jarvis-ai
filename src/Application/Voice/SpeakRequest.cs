namespace JarvisAI.Application.Voice;

public sealed record SpeakRequest(string Text, string? Voice = null, float? Speed = null);
