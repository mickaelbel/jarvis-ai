namespace JarvisAI.Application.Memory;

public sealed class MemoryQuery
{
    public string? Key { get; set; }
    public string? Category { get; set; }
    public MemoryType? Type { get; set; }
    public string? TextSearch { get; set; }
    public MemoryTier? Tier { get; set; }
    public string? Project { get; set; }
    public float? MinImportance { get; set; }
    public bool IncludeExpired { get; set; }
    public int? Limit { get; set; }
    public bool OrderByNewest { get; set; } = true;
}
