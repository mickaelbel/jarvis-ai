namespace JarvisAI.Application.Reasoning;

public enum ThoughtType
{
    Analysis,
    Decision,
    Observation,
    Planning,
    Reflection,
    Conclusion
}

public sealed class ThoughtStep
{
    public int Index { get; set; }
    public ThoughtType Type { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Conclusion { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
