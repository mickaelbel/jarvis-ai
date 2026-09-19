namespace JarvisQARunner;

public enum Verdict { Pass, Partial, Fail }
public enum Difficulty { Easy, Medium, Hard, Adversarial }
public enum Category
{
    Applications,
    Files,
    ComputerUse,
    MultiStep,
    Vision,
    Recovery,
    Ambiguous,
    ConversationAction,
    LongTasks,
    ImpossibleTasks
}

public sealed class UseCase
{
    public string Id { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string ExpectedBehavior { get; init; } = "";
    public Category Category { get; init; }
    public Difficulty Difficulty { get; init; }
    public int TimeoutSeconds { get; set; } = 60;
    public Func<string, List<string>, Verdict>? CustomEvaluator { get; init; }

    public Verdict Evaluate(string response, List<string> toolCalls)
    {
        if (CustomEvaluator is not null)
            return CustomEvaluator(response, toolCalls);

        var lower = response.ToLowerInvariant();
        var hasToolCall = toolCalls.Any(t => t.Contains("✓") || t.Contains("computer_action"));

        // Default evaluation: check if response indicates understanding and action
        if (string.IsNullOrWhiteSpace(response) || response.Length < 10)
            return Verdict.Fail;

        // Check for refusal/honesty
        if (lower.Contains("je ne peux pas") || lower.Contains("impossible") ||
            lower.Contains("je ne suis pas en mesure"))
            return Verdict.Pass; // Honest refusal is a pass for impossible tasks

        // Check for error acknowledgment
        if (lower.Contains("erreur") || lower.Contains("échec") || lower.Contains("failed"))
            return Verdict.Partial;

        return Verdict.Pass;
    }

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}

public sealed class TestResult
{
    public UseCase UseCase { get; init; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Response { get; set; }
    public List<string> ToolCalls { get; set; } = new();
    public Verdict Verdict { get; set; }
    public string? FailureReason { get; set; }
}
