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

            // HUD temps réel : état vocal + texte qui s'écrit pendant le stream.
            _connection.On<string>("overlayState", state =>
            {
                if (_enabled) _window?.SetStatus(state);
            });
            _connection.On<string>("overlayPartial", partial =>
            {
                if (_enabled) _window?.ShowStreaming(partial);
            });
            _connection.On<double>("overlayLevel", level =>
            {
                if (_enabled) _window?.SetLevel(level);
            });
            // HUD : un outil s'exécute → il s'allume dans la barre d'état.
            _connection.On<object>("overlayTool", tool =>
            {
                if (!_enabled) return;
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(tool);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var name = doc.RootElement.GetProperty("name").GetString();
                    var ok = doc.RootElement.GetProperty("success").GetBoolean();
                    _window?.SetToolActivity(name ?? "?", ok);
                }
                catch { }
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
