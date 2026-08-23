namespace JarvisAI.Application.Security;

/// <summary>
/// Stockage persistant des autorisations « toujours autoriser » (outils N2).
/// Révocable à tout moment ; un outil N3 ne peut jamais y figurer.
/// </summary>
public interface IPermissionStore
{
    Task<bool> IsAlwaysAllowedAsync(string toolName, string? action = null, CancellationToken cancellationToken = default);
    Task<bool> AllowAlwaysAsync(string toolName, string? action = null, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(string toolName, string? action = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);
}
