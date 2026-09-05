using JarvisAI.Infrastructure.Vision;
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
    private SplashWindow? _splash;

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

        _splash = new SplashWindow();
        _splash.Show();

        // Le contrôle single-instance est déplacé dans StartupAsync (thread
        // d'arrière-plan) pour ne PAS bloquer le thread UI avec Thread.Sleep.
        _ = Task.Run(StartupAsync);
    }

    /// <summary>
    /// Tente d'activer l'instance déjà en cours : on ne demande l'affichage de sa
    /// fenêtre que si son serveur répond. Retourne true quand l'instance existante
    /// est saine (elle a été réactivée), false sinon.
    /// Version async : ne bloque PAS le thread UI.
    /// </summary>
    private static async Task<bool> TryActivateExistingInstanceAsync()
    {
        var existingUrl = ReadActiveUrl();
        if (string.IsNullOrWhiteSpace(existingUrl)) return false;

        // Laisse jusqu'à ~10 s à l'instance existante pour démarrer son serveur
        // avant de conclure qu'elle est morte : évite de tuer une instance en
        // cours de démarrage si l'utilisateur clique deux fois de suite.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsServerHealthyAsync(existingUrl))
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
            await Task.Delay(500);
        }

        Log("Instance existante mais serveur injoignable après 10 s.");
        return false;
    }

    private static async Task<bool> IsServerHealthyAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync($"{url.TrimEnd('/')}/api/app/status");
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
        // Supprime le fichier active-url AVANT de tuer les processus : sinon le
        // prochain démarrage lira une URL pointant vers un serveur mort et
        // bloquera 10 s sur TryActivateExistingInstance.
        ClearActiveUrl();
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
            // ── Contrôle single-instance (non-bloquant, sur thread d'arrière-plan) ──
            if (!_isFirstInstance)
            {
                if (await TryActivateExistingInstanceAsync())
                {
                    Dispatcher.Invoke(() => Shutdown());
                    return;
                }
                Log("Instance existante mais serveur injoignable — relance complète de Jarvis.");
                KillExistingInstances();
            }

            Environment.CurrentDirectory = AppContext.BaseDirectory;
            ProcessJobGuard.Install();

            UpdateSplash(3, "Préparation de l'environnement...");
            BaseUrl = $"http://127.0.0.1:{FindPreferredPort()}";
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", BaseUrl);
            Log($"Starting server on {BaseUrl} (cwd={Environment.CurrentDirectory})");

            UpdateSplash(12, "Démarrage du serveur web...");
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

            // ── Auto-update GitHub Releases ──
            _ = Task.Run(async () =>
            {
                try
                {
                    var updater = AppUpdaterService.Load();
                    if (!updater.IsConfigured) return;
                    Log($"Auto-update: {updater.Owner}/{updater.Repository}, v={updater.LocalVersion}");
                    var updateUrl = await updater.CheckForUpdateAsync();
                    if (updateUrl is null) return;
                    if (!updater.AutoInstall) return;
                    var launched = await updater.DownloadAndInstallAsync(updateUrl);
                    if (launched) Dispatcher.Invoke(() => Shutdown());
                }
                catch (Exception ex) { Log("Auto-update: " + ex.Message); }
            });

            // ── Windows Integration: Start Menu, auto-start, first-run setup ──
            _ = Task.Run(async () =>
            {
                try
                {
                    var windowsIntegration = _host.Services.GetRequiredService<JarvisAI.Infrastructure.Windows.IWindowsIntegrationService>();
                    windowsIntegration.EnsureDirectories();

                    // Create Start Menu shortcuts on first run
                    var markerFile = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "JarvisAI", ".setup-complete");
                    if (!File.Exists(markerFile))
                    {
                        Log("First run detected - creating Start Menu shortcuts...");
                        windowsIntegration.CreateStartMenuShortcut();
                        windowsIntegration.CreateDesktopShortcut();
                        windowsIntegration.RegisterUninstall();
                        File.WriteAllText(markerFile, DateTime.UtcNow.ToString("O"));
                        Log("Start Menu shortcuts created");
                    }

                    // Voice setup check
                    var voiceSetup = _host.Services.GetService<JarvisAI.Infrastructure.Voice.IVoiceSetupService>();
                    if (voiceSetup is not null)
                    {
                        Log("Running voice setup check...");
                        var report = await voiceSetup.CheckAndSetupAsync();
                        Log($"Voice setup: {report.Status} (Python: {report.PythonFound})");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Windows integration setup failed (non-critical): {ex.Message}");
                }
            });

            // L'interface apparaît dès que le serveur web est prêt, sans attendre le
            // démarrage des services (Ollama, moteur vocal) : on les lance en parallèle
            // pour que leur lenteur ne retarde pas l'affichage de la fenêtre.
            var servicesTask = Task.Run(async () =>
            {
                UpdateSplash(55, "Initialisation du moteur vocal...");
                var svc = new ServiceSupervisor(
                    _host.Services.GetRequiredService<JarvisAI.Infrastructure.AI.OllamaLauncher>());
                await svc.StartAsync();
                _services = svc;
                Log("Services supervisor started");

                // Précharger le modèle en mémoire pour éviter le temps de chargement
                // lors de la première requête utilisateur
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var provider = _host.Services.GetRequiredService<JarvisAI.Infrastructure.AI.OllamaProvider>();
                        if (await provider.IsAvailableAsync())
                        {
                            Log("Preloading model into memory...");
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            // Appel minimal pour forcer le chargement du modèle
                            await provider.ChatAsync(
                                new JarvisAI.Application.AI.AIRequest(
                                    systemPrompt: "ping",
                                    messages: new[] { JarvisAI.Application.AI.AIMessage.User("ping") },
                                    maxTokens: 1));
                            sw.Stop();
                            Log($"Model preloaded in {sw.ElapsedMilliseconds}ms");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Model preload failed (non-critical): {ex.Message}");
                    }
                });

                // Setup automatique ComfyUI + modèle SDXL en arrière-plan
                // (premier lancement uniquement, non-bloquant)
                var comfySetup = _host.Services.GetRequiredService<JarvisAI.Infrastructure.Vision.ComfyUISetupService>();
                comfySetup.StartSetupIfNeeded();
                Log("ComfyUI setup triggered (if needed)");
            });

            await Dispatcher.InvokeAsync(async () =>
            {
                var window = new MainWindow();
                MainWindow = window;
                // Affiche la fenêtre AVANT d'initialiser le WebView2 : le contrôle
                // WebView2 WPF attend une fenêtre visible, sinon son initialisation
                // peut rester bloquée indéfiniment.
                window.Show();
                UpdateSplash(75, "Chargement du moteur web...");
                await window.InitializeAsync(BaseUrl);
                UpdateSplash(90, "Chargement de l'interface...");
                window.SetReady("Prêt");
                if (_startMinimized)
                {
                    window.Hide();
                }
                Log("Ready — " + BaseUrl);

                _splash?.Finish();
                _splash = null;

                // Attend tranquillement (en arrière-plan) la fin des services.
                _ = servicesTask.ContinueWith(_ => { }, TaskScheduler.Default);

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
            // Préfère lancer Chrome réel de l'utilisateur (chemin standard) en lui
            // passant l'URL en argument : Chrome réutilise la fenêtre existante et
            // ouvre un nouvel onglet dans le profil par défaut — jamais une fenêtre
            // « bizarre » ni une session Jarvis dédiée. Repli : navigateur par défaut.
            var chrome = FindChromeExe();
            if (!string.IsNullOrEmpty(chrome))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = chrome,
                    UseShellExecute = true,
                    Arguments = "\"" + url + "\""
                });
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) { Log("Open browser failed: " + ex); }
    }

    private static string? FindChromeExe()
    {
        foreach (var baseDir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (string.IsNullOrEmpty(baseDir)) continue;
            var p = Path.Combine(baseDir, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(p)) return p;
        }
        return null;
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

    private void UpdateSplash(string text) => UpdateSplash(null, text);

    private void UpdateSplash(double? percent, string text)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_splash is null) return;
                if (percent.HasValue) _splash.SetProgress(percent.Value, text);
                else _splash.SetProgress(_splash.CurrentPercent, text);
            });
        }
        catch { }
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

    internal static void ClearActiveUrl()
    {
        try
        {
            if (File.Exists(ActiveUrlPath)) File.Delete(ActiveUrlPath);
        }
        catch { }
    }

    /// <summary>
    /// Libère le mutex single-instance pour qu'une nouvelle instance puisse
    /// devenir first instance (utilisé lors du redémarrage).
    /// </summary>
    internal static void ReleaseMutex()
    {
        try { SingleInstanceMutex.ReleaseMutex(); } catch { }
        ClearActiveUrl();
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
