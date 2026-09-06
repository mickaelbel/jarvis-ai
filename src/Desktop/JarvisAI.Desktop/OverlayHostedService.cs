using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Input;

namespace JarvisAI.Desktop;

/// <summary>
/// Manages overlay windows: text overlay, image overlay, debug overlay.
/// Debug mode: 15 clicks on status dot → password prompt → live reasoning display.
/// </summary>
public sealed class OverlayHostedService : IHostedService, IDisposable
{
    private readonly ILogger<OverlayHostedService> _logger;
    private HubConnection? _connection;
    private OverlayWindow? _window;
    private ImageOverlayWindow? _imageOverlay;
    private DebugOverlayWindow? _debugOverlay;
    private bool _enabled = true;
    private int _statusClickCount;
    private DateTime _lastStatusClick = DateTime.MinValue;

    public OverlayHostedService(ILogger<OverlayHostedService> logger)
    {
        _logger = logger;
    }

    public static OverlayHostedService? Instance { get; private set; }
    public bool DebugMode => _debugOverlay?.IsAuthenticated == true;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Instance = this;
        return Task.CompletedTask;
    }

    public void StartOnUi(string baseUrl)
    {
        try
        {
            _window = new OverlayWindow();
            _window.StatusClicked += () => OnStatusClicked();
            _debugOverlay = new DebugOverlayWindow();

            _connection = new HubConnectionBuilder()
                .WithUrl(baseUrl.TrimEnd('/') + "/hubs/overlay")
                .WithAutomaticReconnect()
                .Build();

            // ── Standard overlay events ────────────────────────────────────
            _connection.On<string, string>("overlayMessage", (title, message) =>
            {
                if (_enabled) _window?.ShowMessage(message, title);
            });

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
            _connection.On<string>("overlayUser", texte =>
            {
                if (_enabled) _window?.ShowUserPartial(texte);
            });
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
                    _debugOverlay?.Log($"Tool: {name} ({(ok ? "OK" : "FAIL")})", "TOOL");
                }
                catch { }
            });

            // ── Debug overlay events ───────────────────────────────────────
            _connection.On<string>("debugLog", message =>
            {
                _debugOverlay?.Log(message, "INFO");
            });
            _connection.On<string, string>("debugLogCategory", (message, category) =>
            {
                _debugOverlay?.Log(message, category);
            });
            _connection.On<string>("debugThinking", thought =>
            {
                _debugOverlay?.Log(thought, "THINK");
            });
            _connection.On<string>("debugFileChange", change =>
            {
                _debugOverlay?.Log(change, "FILE");
            });
            _connection.On<string>("debugResponse", response =>
            {
                _debugOverlay?.Log(response, "RESPONSE");
            });

            // ── Image/Video overlay events ─────────────────────────────────
            _connection.On<string, double?, double?, double?>("overlayImage", (path, x, y, width) =>
            {
                ShowImageOverlay(path, x, y, width);
            });
            _connection.On<string, double?, double?, double?>("overlayVideo", (path, x, y, width) =>
            {
                ShowVideoOverlay(path, x, y, width);
            });
            _connection.On<string[], int, double?, double?>("overlaySlideshow", (paths, interval, x, y) =>
            {
                ShowSlideshow(paths, interval, x, y);
            });
            _connection.On("overlayCloseImage", () =>
            {
                _imageOverlay?.Close();
                _imageOverlay = null;
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
            _logger.LogWarning(ex, "[Overlay] Démarrage impossible");
        }
    }

    /// <summary>
    /// Called when the status dot is clicked. After 15 clicks, prompts for password.
    /// </summary>
    public void OnStatusClicked()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastStatusClick).TotalMilliseconds > 1000)
            _statusClickCount = 0;

        _statusClickCount++;
        _lastStatusClick = now;

        if (_statusClickCount >= 15)
        {
            _statusClickCount = 0;
            PromptDebugPassword();
        }
    }

    private void PromptDebugPassword()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var input = new System.Windows.Controls.TextBox { Width = 200, Margin = new Thickness(8) };
            var prompt = new System.Windows.Controls.StackPanel();
            prompt.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Mot de passe debug :",
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 8)
            });
            prompt.Children.Add(input);

            var window = new System.Windows.Window
            {
                Title = "Debug Mode",
                Width = 300,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.ToolWindow,
                Background = System.Windows.Media.Color.FromArgb(240, 20, 20, 25).ToBrush(),
                Content = prompt
            };

            input.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    if (_debugOverlay?.TryAuthenticate(input.Text) == true)
                    {
                        _debugOverlay.Log("Debug mode activé", "INFO");
                        window.Close();
                    }
                    else
                    {
                        input.Text = "";
                        input.Background = System.Windows.Media.Brushes.Red;
                        System.Threading.Timer? timer = null;
                        timer = new System.Threading.Timer(_ =>
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() => input.Background = System.Windows.Media.Brushes.Transparent);
                            timer?.Dispose();
                        }, null, 500, System.Threading.Timeout.Infinite);
                    }
                }
            };

            window.ShowDialog();
        });
    }

    private void ShowImageOverlay(string path, double? x = null, double? y = null, double? width = null)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _imageOverlay?.Close();
            _imageOverlay = new ImageOverlayWindow(() => _imageOverlay = null);
            _imageOverlay.ShowImage(path, x, y, width);
        });
    }

    private void ShowVideoOverlay(string path, double? x = null, double? y = null, double? width = null)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _imageOverlay?.Close();
            _imageOverlay = new ImageOverlayWindow(() => _imageOverlay = null);
            _imageOverlay.ShowVideo(path, x, y, width);
        });
    }

    private void ShowSlideshow(string[] paths, int intervalMs, double? x, double? y)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _imageOverlay?.Close();
            _imageOverlay = new ImageOverlayWindow(() => _imageOverlay = null);
            _imageOverlay.StartSlideshow(paths, intervalMs, x, y);
        });
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
        _debugOverlay?.Close();
        _imageOverlay?.Close();
        if (_connection is not null)
            return _connection.DisposeAsync().AsTask();
        return Task.CompletedTask;
    }

    public async void Dispose()
    {
        try { if (_connection is not null) await _connection.DisposeAsync(); } catch { }
    }
}
