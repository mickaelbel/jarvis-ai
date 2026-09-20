using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.AutoImprovement;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// "Ameliore Jarvis" tool: the AI analyzes recent errors, proposes code fixes,
/// user approves/rejects, and full rollback is possible.
/// Actions: analyser, proposer, lister, appliquer, rejeter, annuler, historique.
/// </summary>
public sealed class AmelioreJarvisTool : ITool
{
    private readonly ImprovementStore _store;
    private readonly ILogger<AmelioreJarvisTool> _logger;

    public AmelioreJarvisTool(ImprovementStore store, ILogger<AmelioreJarvisTool> logger)
    {
        _store = store;
        _logger = logger;
    }

    public string Name => "ameliorer_jarvis";
    public string Description =>
        "Auto-amelioration de Jarvis : analyser les erreurs, proposer des corrections, " +
        "appliquer/rejeter/annuler des ameliorations. " +
        "Actions : lister (propositions en attente), appliquer <id>, rejeter <id>, annuler <id> (rollback), historique.";
    public string Category => "dev";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "lister | appliquer | rejeter | annuler | historique", typeof(string), required: true),
        new("id", "ID de la proposition (pour appliquer/rejeter/annuler)", typeof(string), required: false)
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("id", out var id);

        var result = (action?.ToLowerInvariant().Trim()) switch
        {
            "lister" => ListPending(),
            "appliquer" => Apply(id),
            "rejeter" => Reject(id),
            "annuler" => Rollback(id),
            "historique" => History(),
            _ => Task.FromResult(ToolResult.Failed("Action inconnue : lister, appliquer, rejeter, annuler, historique."))
        };

        return result;
    }

    private Task<ToolResult> ListPending()
    {
        var pending = _store.GetPendingProposals();
        if (pending.Count == 0)
            return Task.FromResult(ToolResult.Succeeded("Aucune amelioration en attente."));

        var lines = pending.Select(p =>
            $"[{p.Id}] {p.Timestamp:yyyy-MM-dd HH:mm} - {p.Description}\n  Fichier: {p.FilePath}");
        return Task.FromResult(ToolResult.Succeeded(string.Join("\n\n", lines)));
    }

    private Task<ToolResult> Apply(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(ToolResult.Failed("Specifie l'ID de la proposition a appliquer."));

        var ok = _store.ApplyProposal(id);
        if (ok)
        {
            _logger.LogInformation("[Ameliorer] Proposal {Id} applied", id);
            return Task.FromResult(ToolResult.Succeeded($"Amelioration {id} appliquee. Un backup .bak a ete cree."));
        }
        return Task.FromResult(ToolResult.Failed($"Impossible d'appliquer {id} (deja appliquee ou introuvable)."));
    }

    private Task<ToolResult> Reject(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(ToolResult.Failed("Specifie l'ID de la proposition a rejeter."));

        var ok = _store.RejectProposal(id);
        return ok
            ? Task.FromResult(ToolResult.Succeeded($"Amelioration {id} rejetee."))
            : Task.FromResult(ToolResult.Failed($"Impossible de rejeter {id} (deja traitee ou introuvable)."));
    }

    private Task<ToolResult> Rollback(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(ToolResult.Failed("Specifie l'ID de l'amelioration a annuler (rollback)."));

        var ok = _store.RollbackProposal(id);
        if (ok)
        {
            _logger.LogInformation("[Ameliorer] Proposal {Id} rolled back", id);
            return Task.FromResult(ToolResult.Succeeded($"Amelioration {id} annulee. Le code original a ete restaure depuis le backup."));
        }
        return Task.FromResult(ToolResult.Failed($"Rollback impossible pour {id} (pas encore appliquee ou backup manquant)."));
    }

    private Task<ToolResult> History()
    {
        var all = _store.GetAll(20);
        if (all.Count == 0)
            return Task.FromResult(ToolResult.Succeeded("Aucun historique d'amelioration."));

        var lines = all.Select(p =>
            $"[{p.Id}] {p.Timestamp:yyyy-MM-dd HH:mm} | {p.Status,-12} | {p.Description}");
        return Task.FromResult(ToolResult.Succeeded(string.Join("\n", lines)));
    }
}
