namespace JarvisAI.Application.Voice;

public sealed class VoiceSettings
{
    public bool VoiceEnabled { get; set; } = true;
    public bool WakeWordEnabled { get; set; } = true;
    public bool PassiveMode { get; set; }
    public string WakeWords { get; set; } = "jarvis, hey jarvis";
    public bool BargeInEnabled { get; set; } = true;
    public string MicDeviceId { get; set; } = string.Empty;
    public string SpeakerDeviceId { get; set; } = string.Empty;
    public string TtsVoice { get; set; } = "fr_FR-upmc-medium";
    /// <summary>auto (Piper) | xtts (voix clonée, serveur local port 17003).</summary>
    public string TtsEngine { get; set; } = "auto";
    public string TtsLanguage { get; set; } = "fr";
    // Français uniquement : l'auto-détection Whisper partait en anglais/portugais
    // sur de la parole française bruitée.
    public string SttLanguage { get; set; } = "fr";
    public float Volume { get; set; } = 1.0f;
    public float TtsSpeed { get; set; } = 1.0f;
    public bool AutoStart { get; set; } = true;
    public int SilenceTimeoutMs { get; set; } = 800;
    public float VadThreshold { get; set; } = 0.02f;
    public int MaxUtteranceSeconds { get; set; } = 20;
    public string Model { get; set; } = string.Empty;

    // Audio ducking : baisse le volume des autres apps pendant la conversation
    public bool AudioDuckingEnabled { get; set; }
    public float AudioDuckingSystemVolume { get; set; } = 0.3f;
    public float AudioDuckingMusicVolume { get; set; } = 0.1f;
    public int AudioDuckingFadeMs { get; set; } = 1500;
    public string AudioDuckingExcludedApps { get; set; } = string.Empty;
    public string AudioDuckingShortcut { get; set; } = "Ctrl+Shift+D";
    public string[] WakeWordList => WakeWords
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
