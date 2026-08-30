namespace JarvisAI.Application.Security;

/// <summary>
/// Sandbox pour les commandes dangereuses : timeout, restrictions, isollement.
/// </summary>
public interface ISecuritySandbox
{
    Task<SandboxResult> ExecuteInSandboxAsync(string command, SandboxOptions options, CancellationToken ct = default);
    bool IsDangerousCommand(string command);
    SandboxLevel GetRequiredLevel(string command);
}

public sealed record SandboxResult(
    bool Success,
    string Output,
    string Error,
    int ExitCode,
    long DurationMs,
    bool WasKilled);

public sealed record SandboxOptions(
    int TimeoutMs = 30000,
    SandboxLevel Level = SandboxLevel.Normal,
    string? WorkingDirectory = null,
    bool CaptureOutput = true);

public enum SandboxLevel
{
    Normal,     // Commandes sûres
    Restricted, // Commandes modérément risquées (pas de modification système)
    Dangerous,  // Commandes dangereuses (suppression, modification registre)
    Forbidden   // Commandes interdites
}
