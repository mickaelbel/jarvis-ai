namespace JarvisAI.Application.Tools;

public sealed class ToolResult
{
    public bool Success { get; }
    public string Output { get; }
    public string? ErrorMessage { get; }

    private ToolResult(bool success, string output, string? errorMessage)
    {
        Success = success;
        Output = output;
        ErrorMessage = errorMessage;
    }

    public static ToolResult Succeeded(string output) => new(true, output, null);
    public static ToolResult Failed(string errorMessage) => new(false, string.Empty, errorMessage);
}
