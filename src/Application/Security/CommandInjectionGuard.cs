namespace JarvisAI.Application.Security;

/// <summary>
/// Result of a command injection validation. <see cref="Allowed"/> is false when
/// the command contains characters or patterns that could chain or inject
/// secondary commands, or when a command whitelist is configured and the command
/// is not part of it.
/// </summary>
public sealed record CommandValidationResult(bool Allowed, string? Reason)
{
    public static CommandValidationResult Ok() => new(true, null);
    public static CommandValidationResult Blocked(string reason) => new(false, reason);
}

/// <summary>
/// Defense-in-depth guard against shell command injection for the terminal tool.
/// Blocks the metacharacters used to chain, redirect or substitute commands in
/// CMD and PowerShell. Fails closed: any suspicious input is rejected before it
/// reaches the shell.
/// </summary>
public static class CommandInjectionGuard
{
    private const string ChainingMetacharacters = ";&|<>`^";

    /// <summary>
    /// Characters that terminate a string in the shell and therefore never need
    /// to appear in a well-formed command that does not perform redirection or
    /// command substitution.
    /// </summary>
    private const string EnvironmentExpansionMarkers = "$(){}";

    public static CommandValidationResult Validate(string? command, SecurityOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return CommandValidationResult.Ok();

        // Null bytes and line breaks allow truncating or chaining commands.
        if (command.Contains('\0'))
            return CommandValidationResult.Blocked("Null byte detected");
        if (command.Contains('\n') || command.Contains('\r'))
            return CommandValidationResult.Blocked("Line break detected");

        // PowerShell sub-expressions $(...) execute arbitrary code.
        if (command.Contains("$("))
            return CommandValidationResult.Blocked("PowerShell sub-expression detected");

        // Shell chaining / redirection metacharacters.
        foreach (var c in ChainingMetacharacters)
        {
            if (command.Contains(c))
                return CommandValidationResult.Blocked($"Command chaining character '{c}' is not allowed");
        }

        // Environment-expansion markers that can hide a second command.
        foreach (var c in EnvironmentExpansionMarkers)
        {
            if (command.Contains(c))
                return CommandValidationResult.Blocked($"Expansion character '{c}' is not allowed");
        }

        // Optional whitelist: when configured, only the listed commands may run.
        var whitelist = options?.AllowedTerminalCommands;
        if (whitelist is not null && whitelist.Count > 0)
        {
            var commandName = ExtractCommandName(command);
            if (string.IsNullOrEmpty(commandName) || !whitelist.Contains(commandName))
                return CommandValidationResult.Blocked(
                    $"Command '{commandName ?? "(empty)"}' is not in the allowed whitelist");
        }

        return CommandValidationResult.Ok();
    }

    /// <summary>
    /// Extracts the executable name (first whitespace-separated token, quotes and
    /// path stripped). Returns null when the command has no usable first token.
    /// </summary>
    public static string? ExtractCommandName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var trimmed = command.Trim();

        // A leading quoted token: "\"C:\\Program Files\\App\\tool.exe\" --help".
        if (trimmed.Length > 1 && (trimmed[0] == '"' || trimmed[0] == '\''))
        {
            var endQuote = trimmed.IndexOf(trimmed[0], 1);
            if (endQuote > 0)
                return NormalizeName(trimmed.Substring(1, endQuote - 1));
        }

        var firstToken = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(firstToken))
            return null;

        return NormalizeName(firstToken.Trim('"', '\''));
    }

    private static string? NormalizeName(string name)
    {
        try
        {
            return Path.GetFileNameWithoutExtension(name);
        }
        catch
        {
            return name;
        }
    }
}
