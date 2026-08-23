using JarvisAI.Application.Reminders;
using JarvisAI.Application.Voice;
using JarvisAI.Infrastructure.Goals;
using JarvisAI.Infrastructure.Routines;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

/// <summary>
/// Mode proactif : Jarvis initie la conversation quand c'est utile —
/// • rappel imminent (&lt; 20 min) annoncé à voix haute à l'avance ;
/// • relance d'un objectif long terme négligé (&gt; 6 h sans avancée),
///   avec question vocale et exécution immédiate si acceptée.
/// </summary>
public sealed class ProactiveService : BackgroundService
{
    private static readonly TimeSpan FenetreImminent = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan SeuilNegligee = TimeSpan.FromHours(6);
    private const int PeriodeMinutes = 10;

    private readonly IReminderService _reminders;
    private readonly RoutinesStore _routines;
    private readonly ObjectifsStore _objectifs;
    private readonly GoalRunner _goalRunner;
    private readonly IVoiceConfirmationChannel _confirmation;
    private readonly VoiceConversationService _voice;
    private readonly ILogger<ProactiveService> _logger;

    private readonly HashSet<string> _relancesAujourdhui = new(StringComparer.OrdinalIgnoreCase);

    public ProactiveService(
        IReminderService reminders,
        RoutinesStore routines,
        ObjectifsStore objectifs,
        GoalRunner goalRunner,
        IVoiceConfirmationChannel confirmation,
        VoiceConversationService voice,
        ILogger<ProactiveService> logger)
    {
        _reminders = reminders;
        _routines = routines;
        _objectifs = objectifs;
        _goalRunner = goalRunner;
        _confirmation = confirmation;
        _voice = voice;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AnnoncerRappelsImminentsAsync(stoppingToken);
                await RelancerObjectifNegligeAsync(stoppingToken);

                // Routine proactive : "briefing" quotidien si définie par l'utilisateur.
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Proactif] cycle échoué");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(PeriodeMinutes), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Rappels qui arrivent bientôt : annonce anticipée, une seule fois.</summary>
    internal async Task AnnoncerRappelsImminentsAsync(CancellationToken ct)
    {
        var maintenant = DateTime.Now;
        foreach (var rappel in _reminders.GetActive())
        {
            if (rappel.Notified) continue;
            var delta = rappel.DueAt - maintenant;
            if (delta <= TimeSpan.Zero || delta > FenetreImminent) continue;

            var minutes = Math.Max(1, (int)Math.Round(delta.TotalMinutes));
            try
            {
                await _voice.SpeakAsync(
                    $"Petit rappel : dans environ {minutes} minute{(minutes > 1 ? "s" : "")} — {rappel.Text}", ct);
                _reminders.MarkNotified(rappel.Id);
                _logger.LogInformation("[Proactif] rappel imminent annoncé : {Texte} (+{Min} min)", rappel.Text, minutes);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Proactif] annonce impossible pour {Id}", rappel.Id);
            }
        }
    }

    /// <summary>Objectif actif sans avancée récente : proposition vocale de reprendre.</summary>
    internal async Task RelancerObjectifNegligeAsync(CancellationToken ct)
    {
        var objectif = _objectifs.Get().Items.FirstOrDefault(o => o.Statut == "actif");
        if (objectif is null) return;

        var cleRelance = $"{objectif.Titre}:{DateTime.Today:yyyyMMdd}";
        if (_relancesAujourdhui.Contains(cleRelance)) return;
        if (DateTime.UtcNow - objectif.MisAJour < SeuilNegligee) return;

        _relancesAujourdhui.Add(cleRelance);
        _logger.LogInformation("[Proactif] relance objectif négligé : {Titre}", objectif.Titre);

        try
        {
            var reponse = await _confirmation.AskAsync(
                $"Je n'ai pas avancé sur ton objectif « {objectif.Titre} » depuis plus de six heures. Je m'en occupe maintenant ?",
                TimeSpan.FromSeconds(12), ct);

            if (reponse is not { Accepted: true })
            {
                await _voice.SpeakAsync("D'accord, je repasserai plus tard.", ct);
                return;
            }

            var etape = await _goalRunner.RunCycleNowAsync(ct);
            if (!string.IsNullOrWhiteSpace(etape))
                await _voice.SpeakAsync($"C'est fait. Prochaine étape traitée : {etape}.", ct);
            else
                await _voice.SpeakAsync("J'ai fait le tour, rien de bloquant pour le moment.", ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Proactif] relance impossible");
        }
    }
}
