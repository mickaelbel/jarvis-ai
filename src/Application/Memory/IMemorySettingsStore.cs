namespace JarvisAI.Application.Memory;

public interface IMemorySettingsStore
{
    MemorySettings Get();
    void Save(MemorySettings settings);
}
