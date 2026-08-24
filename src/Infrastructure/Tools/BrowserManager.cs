using System.Diagnostics;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Gestionnaire centralisé d'ouverture de liens dans le navigateur.
/// Implémente : limite par session, cooldown, déduplication, journalisation.
/// Toute ouverture de navigateur DOIT passer par ce composant.
/// </summary>
public sealed class BrowserManager : ITool
{
    private readonly ILogger<BrowserManager> _logger;
    private readonly List<BrowserOpenRecord> _history = new();
    private readonly object _lock = new();
    private readonly Action<string>? _processOpener;
    private DateTime _lastOpen = DateTime.MinValue;
    private int _sessionOpens;

    // Limites anti-spam
    private const int MaxAutoOpensPerSession = 3;
    private const double CooldownSeconds = 10;
    private const int MaxSessionTotal = 10;

    public BrowserManager(ILogger<BrowserManager> logger, Action<string>? processOpener = null)
    {
        _logger = logger;
        _processOpener = processOpener;
    }

    public string Name => "browser_manager";
    public string Description => "Gestionnaire sécurisé d'ouverture de navigateur. Actions: open_url, get_history, clear_history, reset_session.";
    public string Category => "browser";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "open_url | get_history | clear_history | reset_session", typeof(string), required: true),
        new ToolParameter("url", "URL à ouvrir", typeof(string)),
        new ToolParameter("confirmed", "true si l'utilisateur a confirmé l'ouverture multiple", typeof(string)),
    };

    public Task<ToolResult> ExecuteAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("url", out var url);
        parameters.TryGetValue("confirmed", out var confirmedStr);
        var confirmed = string.Equals(confirmedStr, "true", StringComparison.OrdinalIgnoreCase);

        var result = (action?.ToLowerInvariant()) switch
        {
            "open_url"      => OpenUrl(url, confirmed),
            "get_history"   => GetHistory(),
            "clear_history" => ClearHistory(),
            "reset_session" => ResetSession(),
            _ => ToolResult.Failed($"Action inconnue : '{action}'. Valides : open_url, get_history, clear_history, reset_session")
        };
        return Task.FromResult(result);
    }

    /// <summary>
    /// Ouvre une URL dans le navigateur par défaut avec toutes les protections.
    /// </summary>
    public ToolResult OpenUrl(string? url, bool confirmed = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Failed("Paramètre 'url' requis.");

        // Ajouter https:// si pas de scheme, MAIS valider d'abord que le scheme
        // éventuel est bien http/https avant tout préfixage.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Si l'utilisateur a écrit "ftp://...:file" par exemple, on ne préfixe pas.
            if (url.Contains("://", StringComparison.Ordinal))
                return ToolResult.Failed($"URL refusée : seuls http/https sont autorisés (scheme détecté : '{url.Split("://")[0]}').");

            url = "https://" + url;
        }

        lock (_lock)
        {
            // 1. Vérification cooldown
            var now = DateTime.UtcNow;
            var secondsSinceLast = (now - _lastOpen).TotalSeconds;
            if (_lastOpen != DateTime.MinValue && secondsSinceLast < CooldownSeconds)
            {
                var wait = (int)(CooldownSeconds - secondsSinceLast) + 1;
                _logger.LogWarning("[BrowserManager] Cooldown actif — attendre {Wait}s", wait);
                return ToolResult.Failed($"Trop tôt pour ouvrir un autre lien. Attendez {wait} seconde(s).");
            }

            // 2. Déduplication — refuser URL déjà ouverte dans la session
            if (_history.Any(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("[BrowserManager] URL déjà ouverte dans cette session : {Url}", url);
                return ToolResult.Failed($"Cette URL a déjà été ouverte dans cette session : {url}");
            }

            // 3. Limite automatique : max 1 auto-open sans confirmation
            if (_sessionOpens >= MaxAutoOpensPerSession && !confirmed)
            {
                _logger.LogWarning("[BrowserManager] Limite automatique atteinte ({Count})", _sessionOpens);
                return ToolResult.Failed(
                    $"Limite d'ouvertures automatiques atteinte ({MaxAutoOpensPerSession}). " +
                    "Passez confirmed=true pour forcer ou demandez confirmation à l'utilisateur.");
            }

            // 4. Limite absolue par session
            if (_sessionOpens >= MaxSessionTotal)
            {
                _logger.LogWarning("[BrowserManager] Limite absolue de session atteinte ({Max})", MaxSessionTotal);
                return ToolResult.Failed($"Limite absolue de session atteinte ({MaxSessionTotal} ouvertures). Réinitialisez la session.");
            }

            // 5. Sécurité : refuser les URLs non-http(s) ou suspects
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ||
                (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
            {
                _logger.LogWarning("[BrowserManager] URL rejetée (scheme non autorisé) : {Url}", url);
                return ToolResult.Failed($"URL refusée : seuls http/https sont autorisés.");
            }

            // Tout est OK : procéder
            _lastOpen = now;
            _sessionOpens++;
            _history.Add(new BrowserOpenRecord(url, now, confirmed));
        }

        try
        {
            // Seam de test : si un processOpener est injecté, on l'appelle au lieu
            // d'ouvrir un vrai navigateur (les tests ne doivent JAMAIS ouvrir d'onglet).
            if (_processOpener is not null)
            {
                _processOpener(url);
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            _logger.LogInformation("[BrowserManager] Ouvert dans le navigateur : {Url}", url);
            return ToolResult.Succeeded($"Ouvert dans le navigateur : {url}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BrowserManager] Échec d'ouverture : {Url}", url);
            // Rollback compteur
            lock (_lock)
            {
                _sessionOpens--;
                _history.RemoveAt(_history.Count - 1);
            }
            return ToolResult.Failed($"Impossible d'ouvrir le navigateur : {ex.Message}");
        }
    }

    private ToolResult GetHistory()
    {
        lock (_lock)
        {
            if (_history.Count == 0)
                return ToolResult.Succeeded("Aucun lien ouvert dans cette session.");
            var lines = _history.Select((h, i) => $"{i + 1}. [{h.OpenedAt:HH:mm:ss}] {h.Url}{(h.Confirmed ? " (confirmé)" : "")}");
            return ToolResult.Succeeded(string.Join("\n", lines));
        }
    }

    private ToolResult ClearHistory()
    {
        lock (_lock) { _history.Clear(); }
        _logger.LogInformation("[BrowserManager] Historique effacé.");
        return ToolResult.Succeeded("Historique de navigation effacé.");
    }

    private ToolResult ResetSession()
    {
        lock (_lock)
        {
            _history.Clear();
            _sessionOpens = 0;
            _lastOpen = DateTime.MinValue;
        }
        _logger.LogInformation("[BrowserManager] Session réinitialisée.");
        return ToolResult.Succeeded("Session de navigation réinitialisée.");
    }

    /// <summary>Nombre d'ouvertures dans la session courante.</summary>
    public int SessionOpens { get { lock (_lock) return _sessionOpens; } }

    /// <summary>Historique en lecture seule.</summary>
    public IReadOnlyList<BrowserOpenRecord> History { get { lock (_lock) return _history.ToList(); } }

    /// <summary>
    /// Réinitialise le cooldown SANS toucher à l'historique ni au compteur.
    /// Utile pour les tests unitaires qui veulent tester plusieurs scénarios.
    /// </summary>
    public void ResetSessionInternal()
    {
        lock (_lock) { _lastOpen = DateTime.MinValue; }
    }
}

public sealed record BrowserOpenRecord(string Url, DateTime OpenedAt, bool Confirmed);
