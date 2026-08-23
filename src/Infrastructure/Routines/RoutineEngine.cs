using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
using JarvisAI.Domain.Events.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Routines;

// ── Modèle ────────────────────────────────────────────────────────────────
public sealed class RoutinesSettings
{
    public List<Routine> Items { get; set; } = new();
}

public sealed class Routine
{
    public string Nom { get; set; } = "";
    /// <summary>presence_retour | presence_depart | horaire</summary>
    public string Declencheur { get; set; } = "";
    /// <summary>"HH:mm" pour le déclencheur horaire.</summary>
    public string Horaire { get; set; } = "";
    public bool Active { get; set; } = true;
    public List<RoutineAction> Actions { get; set; } = new();
}

public sealed class RoutineAction
{
    /// <summary>Nom de l'outil à exécuter (hue, brief, discord…). Vide si simple message vocal.</summary>
    public string Outil { get; set; } = "";
    /// <summary>Paramètres de l'outil en JSON : {"action":"scene","id":"nuit"}</summary>
    public string ArgsJson { get; set; } = "{}";
    /// <summary>Message parlé par Jarvis (peut compléter un outil).</summary>
    public string Message { get; set; } = "";
}

// ── Store (JSON atomique, façon IntegrationsStore) ────────────────────────
public sealed class RoutinesStore
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "routines.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private RoutinesSettings _settings;

    public RoutinesStore() => _settings = Load();

    public RoutinesSettings Get()
    {
        lock (_lock)
            return JsonSerializer.Deserialize<RoutinesSettings>(
                JsonSerializer.Serialize(_settings, JsonOpts), JsonOpts) ?? new();
    }

    public void Save(RoutinesSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            Security.SafeFileWriter.WriteText(Path_, json);
        }
    }

    private RoutinesSettings Load()
    {
        if (!File.Exists(Path_)) return new RoutinesSettings();
        try
        {
            return JsonSerializer.Deserialize<RoutinesSettings>(File.ReadAllText(Path_), JsonOpts) ?? new();
        }
        catch
        {
            return new RoutinesSettings();
        }
    }
}

// ── Moteur : événements présence + planificateur horaire ──────────────────
public sealed class RoutineEngine : BackgroundService
{
    private static readonly string[] Triggers =
        ["presence_retour", "presence_depart"];

    private readonly RoutinesStore _store;
    private readonly IEventBus _eventBus;
    // Lazy : RoutineEngine est aussi une action de RoutinesTool (ITool) — résoudre
    // le registry en direct créerait un cycle ToolRegistry -> RoutinesTool -> ici.
    private readonly Lazy<IToolRegistry> _registry;
    private readonly Lazy<VoiceConversationService> _voice;
    private readonly ILogger<RoutineEngine> _logger;
    private readonly Dictionary<string, DateTime> _dernierRunJournalier = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _subscriptions = new();

    public RoutineEngine(
        RoutinesStore store,
        IEventBus eventBus,
        Lazy<IToolRegistry> registry,
        Lazy<VoiceConversationService> voice,
        ILogger<RoutineEngine> logger)
    {
        _store = store;
        _eventBus = eventBus;
        _registry = registry;
        _voice = voice;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Présence : départ / retour du téléphone
        _eventBus.Subscribe<PresenceChangedEvent>(async (evt, ct) =>
        {
            if (!Triggers.Contains(evt.Kind)) return;
            await RunByTriggerAsync(evt.Kind, stoppingToken);
        });

        // Planificateur horaire : vérification toutes les 30 s, une fois/jour/routine
        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    await RunDueSchedulesAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Routines] erreur planificateur");
                }
            }
        }, stoppingToken);

        _logger.LogInformation("[Routines] Moteur démarré ({Count} routine(s))", _store.Get().Items.Count);
        return Task.CompletedTask;
    }

    internal async Task<int> RunByTriggerAsync(string trigger, CancellationToken ct)
    {
        var actives = _store.Get().Items
            .Where(r => r.Active && r.Declencheur.Equals(trigger, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var routine in actives)
            await RunAsync(routine, ct);
        return actives.Count;
    }

    private async Task RunDueSchedulesAsync(CancellationToken ct)
    {
        var items = _store.Get().Items
            .Where(r => r.Active && r.Declencheur == "horaire")
            .ToList();
        foreach (var routine in items)
        {
            if (!TimeSpan.TryParseExact(routine.Horaire, @"hh\:mm", null, out var cible))
                continue;

            var maintenant = DateTime.Now.TimeOfDay;
            var aujourdHui = DateTime.Today;
            var dejaFait = _dernierRunJournalier.TryGetValue(routine.Nom, out var dernier) && dernier == aujourdHui;

            // Fenêtre de 30 s autour de l'heure demandée, une seule fois par jour
            if (!dejaFait && maintenant >= cible && maintenant < cible.Add(TimeSpan.FromSeconds(30)))
            {
                _dernierRunJournalier[routine.Nom] = aujourdHui;
                await RunAsync(routine, ct);
            }
        }
    }

    public async Task<string> RunByNameAsync(string nom, CancellationToken ct)
    {
        var routine = _store.Get().Items.FirstOrDefault(r =>
            r.Nom.Equals(nom, StringComparison.OrdinalIgnoreCase));
        if (routine is null)
            return $"Routine « {nom} » introuvable.";
        await RunAsync(routine, ct);
        return $"Routine « {routine.Nom} » exécutée.";
    }

    private async Task RunAsync(Routine routine, CancellationToken ct)
    {
        _logger.LogInformation("[Routines] Exécution de « {Nom} » ({Count} action(s))", routine.Nom, routine.Actions.Count);
        foreach (var action in routine.Actions)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(action.Outil))
                {
                    var tool = _registry.Value.GetByName(action.Outil);
                    if (tool is null)
                    {
                        _logger.LogWarning("[Routines] outil inconnu : {Outil}", action.Outil);
                        continue;
                    }
                    Dictionary<string, string>? args = null;
                    try { args = JsonSerializer.Deserialize<Dictionary<string, string>>(action.ArgsJson); }
                    catch { /* args invalides : appel sans paramètres */ }
                    await tool.ExecuteAsync(new AgentContext($"[routine] {routine.Nom}", "routines"), args ?? new());
                }

                if (!string.IsNullOrWhiteSpace(action.Message))
                    await _voice.Value.SpeakAsync(action.Message, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Routines] action échouée dans « {Nom} »", routine.Nom);
            }
        }
    }
}