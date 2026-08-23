using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
using JarvisAI.Infrastructure.Routines;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace JarvisAI.Web.Services;

/// <summary>
/// Auto-création de routines par observation : Jarvis note chaque commande
/// vocale (heure + mots-clés) ; quand la même demande revient ~3 jours
/// différents autour de la même heure, il le fait remarquer et propose de
/// créer la routine. Acceptée à la voix → il s'écrit sa propre automatisation.
/// </summary>
public sealed class HabitsObserverService : BackgroundService
{
    private const string StorePath = "habitudes.json";
    private const int SeuilJours = 3;

    private readonly VoiceConversationService _voice;
    private readonly IVoiceConfirmationChannel _confirmation;
    private readonly RoutinesStore _routines;
    private readonly ILogger<HabitsObserverService> _logger;
    private readonly object _lock = new();
    private List<Observation> _observations = new();
    private readonly HashSet<string> _dejaPropose = new(StringComparer.OrdinalIgnoreCase);

    public HabitsObserverService(
        VoiceConversationService voice,
        IVoiceConfirmationChannel confirmation,
        RoutinesStore routines,
        ILogger<HabitsObserverService> logger)
    {
        _voice = voice;
        _confirmation = confirmation;
        _routines = routines;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Load();
        _voice.UtteranceProcessed += OnUtterance;

        // Vérification douce toutes les 20 minutes (jamais pendant qu'on parle).
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken); }
            catch (OperationCanceledException) { break; }
            try { await DetectAndProposeAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Habitudes] cycle échoué"); }
        }
    }

    private void OnUtterance(VoiceUtteranceRecord record)
    {
        try
        {
            if (record.Status != "success" || string.IsNullOrWhiteSpace(record.Command)) return;
            var cmd = record.Command.Trim();
            if (cmd.Length < 4) return;

            lock (_lock)
            {
                _observations.Add(new Observation(DateTime.Now, Signature(cmd), cmd));
                if (_observations.Count > 500)
                    _observations = _observations[^300..].ToList();
                Save();
            }
        }
        catch { /* jamais bloquant */ }
    }

    internal async Task DetectAndProposeAsync(CancellationToken ct)
    {
        List<(string signature, string exemple, int jours, int heure)> candidates;
        lock (_lock)
        {
            candidates = _observations
                .GroupBy(o => o.Signature)
                .Select(g =>
                {
                    var joursDistincts = g.Select(o => o.Quand.Date).Distinct().Count();
                    var heuresMoyennes = g.Average(o => o.Quand.Hour * 60 + o.Quand.Minute);
                    return (g.Key, g.OrderByDescending(o => o.Quand).First().Exemple,
                            joursDistincts, (int)(heuresMoyennes / 60));
                })
                .Where(c => c.Item3 >= SeuilJours && !_dejaPropose.Contains(c.Key))
                .ToList();
        }

        foreach (var (signature, exemple, jours, heure) in candidates)
        {
            var routines = _routines.Get();
            if (routines.Items.Any(r => r.Commande.Contains(signature, StringComparison.OrdinalIgnoreCase)))
            {
                _dejaPropose.Add(signature);
                continue;
            }

            var nomRoutine = NomDeRoutine(exemple);
            var question = $"Je remarque que depuis {jours} jours tu me demandes à peu près « {exemple} » vers {heure:D2} h. Je crée la routine « {nomRoutine} » pour automatiser ça ?";
            _logger.LogInformation("[Habitudes] proposition : {Nom}", nomRoutine);

            var reponse = await _confirmation.AskAsync(question, TimeSpan.FromSeconds(12), ct);
            _dejaPropose.Add(signature);

            if (reponse is not { Accepted: true }) continue;

            var settings = _routines.Get();
            settings.Items.Add(new Routine
            {
                Nom = nomRoutine,
                Declencheur = "horaire",
                Horaire = $"{heure:D2}:00",
                Active = true,
                Commande = exemple
            });
            _routines.Save(settings);
            await _voice.SpeakAsync($"C'est fait. Routine « {nomRoutine} » créée, tous les jours vers {heure:D2} h.", ct);
            _logger.LogInformation("[Habitudes] routine auto-créée : {Nom} -> {Commande}", nomRoutine, exemple);
        }
    }

    /// <summary>Signature grossière : 3 premiers mots significatifs, sans accents.</summary>
    internal static string Signature(string commande)
    {
        var mots = Regex.Replace(commande.ToLowerInvariant(), @"[^\w\sàâäéèêëîïôöùûüç]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => m.Length > 2 && !EstMotVide(m))
            .Take(3);
        return string.Join(" ", mots);
    }

    private static bool EstMotVide(string mot) => mot is
        "quel" or "quelle" or "est" or "les" or "des" or "une" or "mon" or "ma" or "mes"
        or "pour" or "avec" or "dans" or "sur" or "the" or "and" or "jarvis" or "hey";

    internal static string NomDeRoutine(string commande)
    {
        var sig = Signature(commande);
        var nom = Regex.Replace(sig, @"\s+", "-");
        return string.IsNullOrWhiteSpace(nom) ? "habitude" : $"auto-{nom}"[..40];
    }

    private void Load()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", StorePath);
            if (!File.Exists(path)) return;
            var obs = System.Text.Json.JsonSerializer.Deserialize<List<Observation>>(File.ReadAllText(path));
            if (obs is not null) lock (_lock) _observations = obs;
        }
        catch { /* reprise sur store corrompu */ }
    }

    private void Save()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, StorePath);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(_observations));
        }
        catch { /* best-effort */ }
    }

    internal sealed record Observation(DateTime Quand, string Signature, string Exemple);
}
