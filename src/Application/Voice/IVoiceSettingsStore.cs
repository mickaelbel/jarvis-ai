namespace JarvisAI.Application.Voice;

public interface IVoiceSettingsStore
{
    VoiceSettings Get();
    void Save(VoiceSettings settings);
}
