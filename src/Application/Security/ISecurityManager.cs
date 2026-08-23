using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Application.Security;

public interface ISecurityManager
{
    Task<bool> IsToolAllowedAsync(string toolName, Guid correlationId, CancellationToken cancellationToken = default);
    Task<ConfirmationResult> CheckAndConfirmAsync(string toolName, AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default);
    bool IsCommandBlacklisted(string command);
    bool IsToolWhitelisted(string toolName);
    bool IsPathAllowed(string path);
    SecurityRiskLevel GetToolRiskLevel(string toolName);
    void Configure(SecurityOptions options);
    SecurityOptions GetOptions();
}
