using JarvisAI.Application.Agents;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Gestion des permissions « toujours autoriser ». Autoriser = N2 sensible
/// (confirmation la 1re fois, mémorisable) ; révoquer/liste = N1 sûr.
/// </summary>
public sealed class PermissionsTool : ITool
{
    private readonly IPermissionStore _store;
    private readonly Lazy<IToolRegistry> _registry;
    private readonly ILogger<PermissionsTool> _logger;

    public string Name => "permissions";
    public string Description => "Gère les permissions « toujours autoriser » : liste les autorisations mémorisées (action=list), en ajoute une pour un outil N2 (action=authorize_always + tool), ou révoque (action=revoke + tool). Les actions critiques (extinction du PC…) ne peuvent JAMAIS être mémorisées.";
    public string Category => "security";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Action : list, authorize_always, revoke", typeof(string), required: true),
        new ToolParameter("tool", "Nom de l'outil concerné (pour authorize_always / revoke)", typeof(string)),
        new ToolParameter("tool_action", "Action de l'outil visée, optionnel (ex: click)", typeof(string))
    };

    public PermissionsTool(IPermissionStore store, Lazy<IToolRegistry> registry, ILogger<PermissionsTool> logger)
    {
        _store = store;
        _registry = registry;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("tool", out var tool);
        parameters.TryGetValue("tool_action", out var toolAction);

        try
        {
            switch (action?.ToLowerInvariant())
            {
                case "list":
                {
                    var entries = await _store.ListAsync(cancellationToken);
                    var payload = new
                    {
                        always_allowed = entries,
                        note = "Ces outils ne demandent plus de confirmation. Révoque avec action=revoke."
                    };
                    return ToolResult.Succeeded(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                }
                case "authorize_always":
                {
                    if (string.IsNullOrWhiteSpace(tool))
                        return ToolResult.Failed("Paramètre 'tool' requis");
                    if (_registry.Value.GetByName(tool) is null)
                        return ToolResult.Failed($"Outil inconnu : {tool}");
                    var risk = _registry.Value.GetByName(tool)!.RiskLevel;
                    var level = ToolSafetyPolicy.GetLevel(tool, risk, toolAction);
                    if (level == ToolSafetyLevel.N3)
                        return ToolResult.Failed("Action critique (N3) : impossible de mémoriser « toujours autoriser », je demanderai confirmation à chaque fois.");
                    await _store.AllowAlwaysAsync(tool, toolAction, cancellationToken);
                    return ToolResult.Succeeded($"« {tool} » ne demandera plus de confirmation. ACTION TERMINÉE. Révocable à tout moment avec permissions action=revoke tool={tool}.");
                }
                case "revoke":
                {
                    if (string.IsNullOrWhiteSpace(tool))
                        return ToolResult.Failed("Paramètre 'tool' requis");
                    var removed = await _store.RevokeAsync(tool, toolAction, cancellationToken);
                    return ToolResult.Succeeded(removed
                        ? $"Autorisation de « {tool} » révoquée : confirmation redemandée à la prochaine utilisation. ACTION TERMINÉE."
                        : $"Aucune autorisation mémorisée pour « {tool} ». ACTION TERMINÉE.");
                }
                default:
                    return ToolResult.Failed($"Action inconnue : {action}. Valides : list, authorize_always, revoke");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Permissions] Action {Action} échouée", action);
            return ToolResult.Failed($"Erreur permissions : {ex.Message}");
        }
    }
}
