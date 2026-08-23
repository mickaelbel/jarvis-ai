namespace JarvisAI.Application.Commands;

public sealed class CommandResult
{
    public bool Success { get; }
    public string Response { get; }
    public string? ToolUsed { get; }
    public string? ErrorMessage { get; }

    private CommandResult(bool success, string response, string? toolUsed, string? errorMessage)
    {
        Success = success;
        Response = response;
        ToolUsed = toolUsed;
        ErrorMessage = errorMessage;
    }

    public static CommandResult Succeeded(string response, string? toolUsed = null)
        => new(true, response, toolUsed, null);

    public static CommandResult Failed(string errorMessage)
        => new(false, errorMessage, null, errorMessage);
}
