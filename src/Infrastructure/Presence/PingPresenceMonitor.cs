using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Presence;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.NetworkInformation;

namespace JarvisAI.Infrastructure.Presence;

public sealed class PingPresenceMonitor : BackgroundService
{
    private readonly PresenceOptions _options;
    private readonly IEventBus _eventBus;
    private readonly ILogger<PingPresenceMonitor> _logger;

    private readonly object _lock = new();
    private bool _enabled;
    private bool _present;
    private bool _stateInitialized;
    private DateTime _absentSince;
    private bool _departureTriggered;

    public PingPresenceMonitor(PresenceOptions options, IEventBus eventBus, ILogger<PingPresenceMonitor> logger)
    {
        _options = options;
        _eventBus = eventBus;
        _logger = logger;
        _enabled = options.Enabled && !string.IsNullOrWhiteSpace(options.PhoneIp);
    }

    public bool IsEnabled { get { lock (_lock) return _enabled; } }
    public bool IsConfiguredPublic => !string.IsNullOrWhiteSpace(_options.PhoneIp);
    public bool? IsPresent { get { lock (_lock) return _stateInitialized ? _present : null; } }

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _enabled = enabled && !string.IsNullOrWhiteSpace(_options.PhoneIp);
            if (!enabled)
            {
                _stateInitialized = false;
                _departureTriggered = false;
            }
        }
        _logger.LogInformation("[Presence] Détection {State}", enabled ? "activée" : "désactivée");
    }

    public string GetStatus()
    {
        lock (_lock)
        {
            if (!_enabled) return "Détection de présence désactivée. ACTION TERMINÉE.";
            if (!_stateInitialized) return "Détection active, premier sondage en cours… ACTION TERMINÉE.";
            var since = _present ? "" : $" (absent depuis {Math.Round((DateTime.UtcNow - _absentSince).TotalMinutes)} min)";
            return $"Téléphone {(_present ? "présent" : "absent")}{since}. ACTION TERMINÉE.";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsConfigured())
        {
            _logger.LogInformation("[Presence] Pas d'IP téléphone configurée (JarvisAI:Presence:PhoneIp) — détection inactive");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.IntervalSeconds));
        _logger.LogInformation("[Presence] Surveillance de {Ip} toutes les {S}s (seuil absence {T}s)",
            _options.PhoneIp, interval.TotalSeconds, _options.AbsenceThresholdSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                if (!IsEnabled) continue;

                var reachable = IsReachable(_options.PhoneIp);
                OnPingResult(reachable);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Presence] Erreur de ping");
            }
        }
    }

    internal void OnPingResult(bool reachable)
    {
        bool departureNow = false, returnNow = false;
        lock (_lock)
        {
            if (!_enabled) return;

            if (!_stateInitialized)
            {
                _present = reachable;
                _stateInitialized = true;
                if (!reachable) _absentSince = DateTime.UtcNow;
                _logger.LogInformation("[Presence] État initial : téléphone {State}", reachable ? "présent" : "absent");
                return;
            }

            if (reachable && !_present)
            {
                _present = true;
                _departureTriggered = false;
                returnNow = true;
            }
            else if (!reachable && _present)
            {
                _present = false;
                _absentSince = DateTime.UtcNow;
            }
            else if (!reachable && !_present && !_departureTriggered)
            {
                if ((DateTime.UtcNow - _absentSince).TotalSeconds >= _options.AbsenceThresholdSeconds)
                {
                    _departureTriggered = true;
                    departureNow = true;
                }
            }
        }

        if (returnNow)
        {
            _logger.LogInformation("[Presence] Retour détecté");
            _ = PublishAsync("presence_return", "L'utilisateur est rentré.");
        }
        else if (departureNow)
        {
            _logger.LogInformation("[Presence] Départ détecté");
            _ = PublishAsync("presence_departure", "L'utilisateur est parti.");
        }
    }

    private async Task PublishAsync(string kind, string detail)
    {
        try
        {
            await _eventBus.PublishAsync(new PresenceChangedEvent(kind, detail), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Presence] Publication événement échouée");
        }
    }

    private static bool IsReachable(string ip)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(ip, 800);
                if (reply is { Status: IPStatus.Success })
                    return true;
            }
            catch
            {
            }
        }
        return false;
    }

    private bool IsConfigured() => !string.IsNullOrWhiteSpace(_options.PhoneIp);
}
