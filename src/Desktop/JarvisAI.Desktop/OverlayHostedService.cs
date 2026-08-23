using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Desktop;

/// <summary>
/// Connecte l'overlay flottant au serveur local (/hubs/overlay) et affiche les
/// réponses de Jarvis dans une mini-fenêtre sans vol de focus. L'overlay est
/// optionnel : toute erreur de connexion reste silencieuse.
/// </summary>
public sealed class OverlayHostedService : IHostedService, IDisposable
{
    private readonly ILogger<OverlayHostedService> _logger;
    private HubConnection? _connection;
    private OverlayWindow? _window;
    private bool _enabled = true;

    public OverlayHostedService(ILogger<OverlayHostedService> logger)
    {
        _logger = logger;
    }

    public static OverlayHostedService? Instance { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Instance = this;
        return Task.CompletedTask;
    }

    /// <summary>Appelé sur le thread UI une fois la fenêtre principale prête.</summary>
    public void StartOnUi(string baseUrl)
    {
        try
        {
            _window = new OverlayWindow();

            _connection = new HubConnectionBuilder()
                .WithUrl(baseUrl.TrimEnd('/') + "/hubs/overlay")
                .WithAutomaticReconnect()
                .Build();

            _connection.On<string, string>("overlayMessage", (title, message) =>
            {
                if (_enabled)
                    _window?.ShowMessage(message, title);
            });

            _connection.Closed += async (error) =>
            {
                if (error is not null) App.Log("Overlay connection closed: " + error.Message);
                await Task.Delay(TimeSpan.FromSeconds(5));
                try { await _connection.StartAsync(); } catch { }
            };

            _ = Task.Run(async () =>
            {
                while (_connection.State != HubConnectionState.Connected)
                {
                    try
                    {
                        await _connection.StartAsync();
                        _logger.LogInformation("[Overlay] Connecté à /hubs/overlay");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[Overlay] Connexion en attente du serveur");
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Overlay] Démarrage impossible (fonctionnalité optionnelle)");
        }
    }

    public void Toggle()
    {
        _enabled = !_enabled;
        if (!_enabled) _window?.HideOverlay();
        App.Log("Overlay " + (_enabled ? "activé" : "désactivé"));
    }

    public bool IsEnabled => _enabled;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
            return _connection.DisposeAsync().AsTask();
        return Task.CompletedTask;
    }

    public async void Dispose()
    {
        try { if (_connection is not null) await _connection.DisposeAsync(); } catch { }
    }
}
