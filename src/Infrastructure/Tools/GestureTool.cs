using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

// Gestes de la main (docs/gestes.md) : MediaPipe dans un sous-process isolé.
// Le tracker n'envoie QUE des labels (« main_ouverte », « poing »…) vers
// /api/gestes — aucune image ne quitte le PC. Mapping par défaut N1 :
//   main_ouverte → play/pause · pincement_haut/bas → volume ± · poing → mute
public sealed class GestureTool : ITool
{
    private readonly IntegrationsStore _store;
    private readonly ILogger<GestureTool> _logger;
    private Process? _tracker;

    public GestureTool(IntegrationsStore store, ILogger<GestureTool> logger)
    {
        _store = store;
        _logger = logger;
    }

    public string Name => "gestes";
    public string Description =>
        "Gestes de la main via webcam (100 % local, MediaPipe isolé) : pilote média sans toucher le PC. " +
        "Actions : start (active), stop (arrête), status. Requiert scripts/setup_gestes.py lancé une fois.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "start | stop | status", typeof(string), required: true)
    };

    private static (string? Tracker, string? Python) FindTracker()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++)
        {
            var tracker = Path.Combine(current.FullName, "gestes", "tracker.py");
            var python = Path.Combine(current.FullName, "gestes", ".venv-tracker", "Scripts", "python.exe");
            if (File.Exists(tracker))
                return (tracker, File.Exists(python) ? python : null);
            current = current.Parent;
        }
        return (null, null);
    }

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        try
        {
            var result = (action?.ToLowerInvariant()) switch
            {
                "start" => Start(),
                "stop" => Stop(),
                "status" => ToolResult.Succeeded(_tracker is not null && !_tracker.HasExited
                    ? "Tracking gestes ACTIF (caméra allumée). ACTION TERMINÉE."
                    : "Tracking gestes inactif."),
                _ => ToolResult.Failed($"Action inconnue : {action}. Valides : start, stop, status")
            };
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Gestes] {Action} échoué", action);
            return Task.FromResult(ToolResult.Failed($"Erreur gestes : {ex.Message}"));
        }
    }

    private ToolResult Start()
    {
        if (_tracker is not null && !_tracker.HasExited)
            return ToolResult.Succeeded("Le tracking gestes est déjà actif.");

        var (tracker, python) = FindTracker();
        if (tracker is null)
            return ToolResult.Failed("tracker.py introuvable.");
        if (python is null)
            return ToolResult.Failed("Venv gestes absent : lance « py -3.11 scripts/setup_gestes.py » une fois puis réessaie.");

        var cfg = _store.Get().Gestes;
        var conf = JsonSerializer.Serialize(new
        {
            device = cfg.Device,
            api_url = "http://127.0.0.1:51844/api/gestes",
            token = cfg.Token
        });

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{tracker}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.EnvironmentVariables["GESTES_CONF"] = conf;

        _tracker = Process.Start(psi);
        if (_tracker is null)
            return ToolResult.Failed("Impossible de lancer le tracker gestes.");

        _ = Task.Run(async () =>
        {
            var err = await _tracker.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(err))
                _logger.LogWarning("[Gestes] tracker stderr : {Err}", err.Trim());
        });

        _logger.LogInformation("[Gestes] Tracking démarré (PID {Pid})", _tracker.Id);
        return ToolResult.Succeeded("Gestes activés — lève la main ouverte pour play/pause, pince vers le haut/bas pour le volume. ACTION TERMINÉE.");
    }

    private ToolResult Stop()
    {
        if (_tracker is null || _tracker.HasExited)
        {
            _tracker = null;
            return ToolResult.Failed("Le tracking gestes n'est pas actif.");
        }
        try
        {
            _tracker.Kill(true);
            _logger.LogInformation("[Gestes] Tracking arrêté");
            return ToolResult.Succeeded("Gestes désactivés. ACTION TERMINÉE.");
        }
        finally
        {
            _tracker.Dispose();
            _tracker = null;
        }
    }
}