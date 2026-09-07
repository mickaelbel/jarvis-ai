namespace JarvisAI.Application.Tools;

public sealed class ToolResult
{
    public bool Success { get; }
    public string Output { get; }
    public string? ErrorMessage { get; }
    public string? ToolName { get; }
    public long DurationMs { get; }
    public DateTime Timestamp { get; } = DateTime.UtcNow;

    private ToolResult(bool success, string output, string? errorMessage, string? toolName = null, long durationMs = 0)
    {
        Success = success;
        Output = output;
        ErrorMessage = errorMessage;
        ToolName = toolName;
        DurationMs = durationMs;
    }

    public static ToolResult Succeeded(string output) => new(true, output, null);
    public static ToolResult Failed(string errorMessage) => new(false, string.Empty, errorMessage);

    public ToolResult WithMeta(string toolName, long durationMs)
        => new(Success, Output, ErrorMessage, toolName, durationMs);

    public static ToolResult Succeeded(string toolName, string output, long durationMs)
        => new(true, output, null, toolName, durationMs);
    public static ToolResult Failed(string toolName, string errorMessage, long durationMs)
        => new(false, string.Empty, errorMessage, toolName, durationMs);
}
