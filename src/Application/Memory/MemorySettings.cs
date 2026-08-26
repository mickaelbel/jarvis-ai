namespace JarvisAI.Application.Memory;

public sealed class MemorySettings
{
    public bool MemoryEnabled { get; set; } = true;
    public bool RecordEpisodes { get; set; } = true;
    public bool AutoSaveFacts { get; set; } = true;
    public int MaxEpisodesDays { get; set; } = 30;
    public int MaxLongTermMemories { get; set; } = 200;
}
