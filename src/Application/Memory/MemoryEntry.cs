namespace JarvisAI.Application.Memory;

public sealed class MemoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public MemoryType Type { get; set; }
    public string Category { get; set; } = string.Empty;
    public MemoryTier Tier { get; set; } = MemoryTier.LongTerm;
    public string ProjectName { get; set; } = string.Empty;
    public float Importance { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public int AccessCount { get; set; }
    public float[]? Embedding { get; set; }
}
