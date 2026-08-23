namespace JarvisAI.Application.Voice;

public sealed record VoiceTestRequest(
    string Text,
    string Voice = "fr_FR-upmc-medium",
    string Engine = "piper",
    float Volume = 1.0f,
    float Speed = 1.0f);
