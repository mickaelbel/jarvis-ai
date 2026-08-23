using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;

namespace JarvisAI.Desktop;

public partial class App : System.Windows.Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "desktop.log");

    private static readonly string ActiveUrlPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "active-url.txt");

    private static readonly bool _isFirstInstance;
    private static readonly Mutex SingleInstanceMutex =
        new(initiallyOwned: true, "JarvisAI.Desktop.SingleInstance", out _isFirstInstance);

    private static readonly string ShowWindowEventName = "JarvisAI.ShowWindow";
    private static EventWaitHandle? _showWindowEvent;

    private WebApplication? _host;
    private ServiceSupervisor? _services;
    private GlobalHotkeys? _hotkeys;
    private bool _startMinimized;

    public static string BaseUrl { get; private set; } = "http://127.0.0.1:51844";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            Log("DispatcherUnhandledException: " + args.Exception);
            System.Windows.MessageBox.Show("Erreur Jarvis : " + args.Exception.Message, "Jarvis AI",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("UnhandledException: " + args.ExceptionObject);

        _startMinimized = e.Args.Contains("--autostart");

        _showWindowEvent = CreateShowWindowEvent();

        if (!_isFirstInstance)
        {
            // Une autre instance est déjà active : on vérifie d'abord qu'elle est
            // réellement fonctionnelle (serveur joignable). Si oui, on lui demande
            // de réafficher sa fenêtre et on se ferme immédiatement. Si son serveur
            // ne répond plus (processus zombie, page « 127 non dispo »), on la
            // termine et on relance Jarvis proprement à la place.
            if (TryActivateExistingInstance())
            {
                Shutdown();
                return;
            }

            Log("Instance existante mais serveur injoignable — relance complète de Jarvis.");
            KillExistingInstances();
        }

        _ = Task.Run(StartupAsync);
    }

    /// <summary>
    /// Tente d'activer l'instance déjà en cours : on ne demande l'affichage de sa
    /// fenêtre que si son serveur répond. Retourne true quand l'instance existante
    /// est saine (elle a été réactivée), false sinon.
    /// </summary>
    private static bool TryActivateExistingInstance()
    {
        var existingUrl = ReadActiveUrl();
        if (string.IsNullOrWhiteSpace(existingUrl)) return false;

        // Laisse jusqu'à ~10 s à l'instance existante pour démarrer son serveur
        // avant de conclure qu'elle est morte : évite de tuer une instance en
        // cours de démarrage si l'utilisateur clique deux fois de suite.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (IsServerHealthy(existingUrl))
            {
                Log("Une instance de Jarvis est déjà active et joignable — réaffichage de sa fenêtre.");
                if (_showWindowEvent is not null)
                {
                    _showWindowEvent.Set();
                }
                else
                {
                    OpenBrowser(existingUrl);
                }
                return true;
            }
            Thread.Sleep(500);
        }

        Log("Instance existante mais serveur injoignable après 10 s.");
        return false;
    }

    private static bool IsServerHealthy(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = client.GetAsync($"{url.TrimEnd('/')}/api/app/status").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Termine les autres instances de Jarvis (même exécutable) dont le serveur
    /// est mort, pour laisser la place à une relance propre.
    /// </summary>
    private static void KillExistingInstances()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            var currentId = current.Id;
            var myExe = current.MainModule?.FileName;

            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == currentId) continue;
                try
                {
                    var otherExe = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(myExe) && !string.IsNullOrEmpty(otherExe) &&
                        !string.Equals(myExe, otherExe, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                catch { }

                try
                {
                    Log($"Arrêt de l'instance inutilisable (PID {process.Id}).");
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log("KillExistingInstances: " + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log("KillExistingInstances: " + ex.Message);
        }
    }

    private async Task StartupAsync()
    {
        try
        {
            // Le serveur web sert wwwroot depuis le répertoire courant : on fixe le
            // répertoire de travail sur le dossier de l'exécutable pour que les
            // assets statiques (css/js) soient trouvés quel que soit le mode de
            // lancement (raccourci, autostart, invite de commandes, ...).
            Environment.CurrentDirectory = AppContext.BaseDirectory;

            // Kill-on-close : au crash de Jarvis, le noyau termine tous les
            // processus enfants (serveurs voix, gestes, navigateurs…).
            ProcessJobGuard.Install();

            BaseUrl = $"http://127.0.0.1:{FindPreferredPort()}";
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", BaseUrl);
            Log($"Starting server on {BaseUrl} (cwd={Environment.CurrentDirectory})");

            _host = JarvisAI.Web.WebAppFactory.Create(null, builder =>
            {
                builder.Services.AddHostedService<VoiceHostedService>();
                builder.Services.AddHostedService<OverlayHostedService>();
                builder.Services.AddSingleton<JarvisAI.Application.Voice.IAudioDeviceLister, AudioDeviceLister>();
                builder.Services.AddSingleton<JarvisAI.Web.Services.IAppLifecycleService, DesktopAppLifecycle>();
                // Auto-développement : même instance, vue via l'abstraction Infrastructure
                builder.Services.AddSingleton<JarvisAI.Infrastructure.Dev.ISelfDevLifecycle>(sp =>
                    (JarvisAI.Infrastructure.Dev.ISelfDevLifecycle)sp.GetRequiredService<JarvisAI.Web.Services.IAppLifecycleService>());

                // Confirmation de sécurité des outils à risque : question posée à
                // voix haute et réponse écoutée (canal vocal), sinon modale web.
                builder.Services.AddSingleton<JarvisAI.Application.Security.IUserConfirmationService>(sp =>
                    new JarvisAI.Infrastructure.Security.VoiceConfirmationService(
                        sp.GetRequiredService<ILogger<JarvisAI.Infrastructure.Security.VoiceConfirmationService>>(),
                        voiceChannel: new Lazy<JarvisAI.Application.Voice.IVoiceConfirmationChannel?>(
                            () => sp.GetService<JarvisAI.Application.Voice.IVoiceConfirmationChannel>()),
                        fallback: sp.GetRequiredService<JarvisAI.Application.Security.WebConfirmationService>(),
                        options: sp.GetService<JarvisAI.Application.Security.SecurityOptions>()));
            });
            await _host.StartAsync();
            Log("Server started (voice engine hosted)");
            SaveActiveUrl();

            _services = new ServiceSupervisor(
                _host.Services.GetRequiredService<JarvisAI.Infrastructure.AI.OllamaLauncher>());
            await _services.StartAsync();
            Log("Services supervisor started");

            await Dispatcher.InvokeAsync(async () =>
            {
                var window = new MainWindow();
                MainWindow = window;
                // Affiche la fenêtre AVANT d'initialiser le WebView2 : le contrôle
                // WebView2 WPF attend une fenêtre visible, sinon son initialisation
                // peut rester bloquée indéfiniment.
                window.Show();
                await window.InitializeAsync(BaseUrl);
                window.SetReady("Prêt");
                if (_startMinimized)
                {
                    window.Hide();
                }
                Log("Ready — " + BaseUrl);

                // Overlay flottant : fenêtre sans focus qui affiche les réponses.
                foreach (var overlay in _host.Services.GetServices<OverlayHostedService>())
                    overlay.StartOnUi(BaseUrl);

                // Raccourci global push-to-talk (Ctrl+Alt+J)
                _hotkeys = new GlobalHotkeys();
                _hotkeys.Start(() => VoiceHostedService.Engine?.TriggerPushToTalk());

                _ = Task.Run(WatchShowWindowRequests);
            });
        }
        catch (Exception ex)
        {
            Log("Startup failed: " + ex);
            try
            {
                await Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show("Jarvis n'a pas pu démarrer :\n" + ex.Message, "Jarvis AI",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error));
            }
            catch { }
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _services?.Stop();
        if (_services is not null) await _services.DisposeAsync();
        if (_host is not null)
        {
            try { await _host.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
            ((IDisposable)_host).Dispose();
        }
        ClearActiveUrl();
        Log("Exiting");
        base.OnExit(e);
    }

    public static void OpenBrowser()
    {
        OpenBrowser(BaseUrl);
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) { Log("Open browser failed: " + ex); }
    }

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled)
                key.SetValue("JarvisAI", $"\"{Environment.ProcessPath}\" --autostart");
            else
                key.DeleteValue("JarvisAI", false);
        }
        catch (Exception ex) { Log("AutoStart: " + ex); }
    }

    public static bool IsAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("JarvisAI") is not null;
        }
        catch { return false; }
    }

    public static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (dir is not null) Directory.CreateDirectory(dir);

            // Rotation simple : au-delà de 5 Mo, le journal courant devient .old
            try
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 5 * 1024 * 1024)
                {
                    var old = LogPath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogPath, old);
                }
            }
            catch { }

            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static EventWaitHandle? CreateShowWindowEvent()
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        }
        catch (Exception ex)
        {
            Log("ShowWindowEvent: " + ex.Message);
            return null;
        }
    }

    private void WatchShowWindowRequests()
    {
        var evt = _showWindowEvent;
        if (evt is null) return;
        while (true)
        {
            try { evt.WaitOne(); }
            catch { return; }
            Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is MainWindow w) w.ShowFromTray();
            });
        }
    }

    private static void SaveActiveUrl()
    {
        try
        {
            var dir = Path.GetDirectoryName(ActiveUrlPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(ActiveUrlPath, BaseUrl);
        }
        catch (Exception ex)
        {
            Log("SaveActiveUrl: " + ex.Message);
        }
    }

    private static string? ReadActiveUrl()
    {
        try
        {
            return File.Exists(ActiveUrlPath) ? File.ReadAllText(ActiveUrlPath).Trim() : null;
        }
        catch (Exception ex)
        {
            Log("ReadActiveUrl: " + ex.Message);
            return null;
        }
    }

    private static void ClearActiveUrl()
    {
        try
        {
            if (File.Exists(ActiveUrlPath)) File.Delete(ActiveUrlPath);
        }
        catch { }
    }

    private static int FindPreferredPort()
    {
        for (var port = 51844; port < 51860; port++)
        {
            if (IsPortFree(port)) return port;
        }
        return FindFreePort();
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
