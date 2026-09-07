using JarvisAI.Application.Agents;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Application.Tools;

/// <summary>
/// Classe de base pour les outils. Fournit :
/// - Gestion d'erreurs standardisée (try/catch avec logging)
/// - Chronométrage automatique (temps d'exécution loggé)
/// - Validation de paramètres
/// - Factory methods pour ToolResult
///
/// Utilisation : hériter de ToolBase et implémenter ExecuteCoreAsync.
/// </summary>
public abstract class ToolBase : ITool
{
    protected readonly ILogger Logger;

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string Category { get; }
    public virtual SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public virtual bool McpExpose => false;
    public virtual string? WaitingPhrase => null;
    public virtual bool IsAvailable => true;
    public virtual IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

    protected ToolBase(ILogger logger)
    {
        Logger = logger;
    }

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Timeout spécifique à cet outil (par défaut 60s). Les outils lents (browser,
    /// vision, terminal) peuvent le surcharger, les outils rapides (calculatrice) le réduire.</summary>
    public virtual TimeSpan Timeout => DefaultTimeout;

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);
            var result = await ExecuteCoreAsync(context, parameters, timeoutCts.Token);
            sw.Stop();
            Logger.LogInformation("[{Tool}] Exécuté en {Ms}ms", Name, sw.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            Logger.LogWarning("[{Tool}] Timeout après {Ms}ms (limit: {Limit}s)", Name, sw.ElapsedMilliseconds, Timeout.TotalSeconds);
            return ToolResult.Failed($"Timeout après {Timeout.TotalSeconds:F0}s.");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Logger.LogWarning("[{Tool}] Annulé après {Ms}ms", Name, sw.ElapsedMilliseconds);
            return ToolResult.Failed("Opération annulée.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Logger.LogError(ex, "[{Tool}] Erreur après {Ms}ms", Name, sw.ElapsedMilliseconds);
            return ToolResult.Failed($"Erreur : {ex.Message}");
        }
    }

    /// <summary>
    /// Logique métier de l'outil. Les exceptions sont attrapées automatiquement.
    /// </summary>
    protected abstract Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken);

    /// <summary>Validation qu'un paramètre requis est présent.</summary>
    protected static string RequireParam(IReadOnlyDictionary<string, string> parameters, string name)
    {
        if (parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        throw new ArgumentException($"Paramètre requis manquant : '{name}'.");
    }

    /// <summary>Raccourci pour ToolResult.Succeeded.</summary>
    protected static ToolResult Ok(string message) => ToolResult.Succeeded(message);

    /// <summary>Raccourci pour ToolResult.Failed.</summary>
    protected static ToolResult Fail(string message) => ToolResult.Failed(message);
}
