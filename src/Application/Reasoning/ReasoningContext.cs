namespace JarvisAI.Application.Reasoning;

public sealed class ReasoningContext
{
    public string Goal { get; set; } = string.Empty;
    public List<ThoughtStep> Thoughts { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
    public List<string> AvailableTools { get; set; } = new();
    public string? CurrentConclusion { get; set; }
    public bool IsComplete { get; set; }

    public void AddThought(ThoughtStep step)
    {
        step.Index = Thoughts.Count;
        Thoughts.Add(step);
    }

    public string GetReasoningTrace()
    {
        return string.Join("\n", Thoughts.Select(t =>
            $"[{t.Type}] Step {t.Index}: {t.Content}" +
            (t.Conclusion != null ? $" -> {t.Conclusion}" : "")));
    }
}
