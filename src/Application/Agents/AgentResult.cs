namespace JarvisAI.Application.Agents;

public sealed class AgentResult
{
    public bool Success { get; }
    public string Response { get; }
    public string? ToolUsed { get; }
    public Exception? Error { get; }
    public TimeSpan Duration { get; }

    private AgentResult(bool success, string response, string? toolUsed, Exception? error, TimeSpan duration)
    {
        Success = success;
        Response = response;
        ToolUsed = toolUsed;
        Error = error;
        Duration = duration;
    }

    public static AgentResult Succeeded(string response, string? toolUsed, TimeSpan duration)
        => new(true, response, toolUsed, null, duration);

    public static AgentResult Failed(string response, Exception error, TimeSpan duration)
        => new(false, response, null, error, duration);
}
