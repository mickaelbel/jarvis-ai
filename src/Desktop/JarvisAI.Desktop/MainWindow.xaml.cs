using Microsoft.Web.WebView2.Core;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace JarvisAI.Desktop;

public partial class MainWindow : Window
{
    private NotifyIcon? _tray;
    private bool _quitting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        Icon = LoadWindowIcon();
        CreateTrayIcon();
    }

    public void SetReady(string message) => Title = "Jarvis AI · " + message;

    public async Task InitializeAsync(string url)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", "WebView2");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userData);
            await WebView.EnsureCoreWebView2Async(environment);

            // Masque la barre d'état en bas à gauche (URL affichée au survol des liens).
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Les liens qui s'ouvrent dans un nouvel onglet (vidéos YouTube, etc.)
            // doivent partir dans le navigateur par défaut, pas dans la fenêtre.
            WebView.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                App.OpenBrowser(e.Uri);
            };

            // WebView2 refuse micro/caméra par défaut : on autorise pour que les
            // fonctions vocales du navigateur (voix de secours, liste des périph.)
            // restent utilisables dans la fenêtre d'application.
            WebView.CoreWebView2.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind is CoreWebView2PermissionKind.Microphone
                    or CoreWebView2PermissionKind.Camera)
                {
                    e.State = CoreWebView2PermissionState.Allow;
                }
            };

            WebView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            App.Log("WebView2 init failed: " + ex);
            Title = "Jarvis AI · Erreur WebView2 : " + ex.Message;
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void CreateTrayIcon()
    {
        var autoStart = new ToolStripMenuItem("Lancer au démarrage")
        {
            Checked = App.IsAutoStart(),
            CheckOnClick = true
        };
        autoStart.CheckedChanged += (_, _) => App.SetAutoStart(autoStart.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir Jarvis", null, (_, _) => ShowFromTray());
        menu.Items.Add("Recharger l'interface", null, (_, _) => Reload());
        menu.Items.Add("Ouvrir dans le navigateur", null, (_, _) => App.OpenBrowser());
        var overlayItem = new ToolStripMenuItem("Overlay des réponses")
        {
            Checked = OverlayHostedService.Instance?.IsEnabled ?? true,
            CheckOnClick = true
        };
        overlayItem.Click += (_, _) => OverlayHostedService.Instance?.Toggle();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(overlayItem);
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Jarvis AI",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    public void Reload()
    {
        if (WebView.CoreWebView2 is not null) WebView.CoreWebView2.Reload();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return System.Drawing.Icon.ExtractAssociatedIcon(path) ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
        }
        return System.Drawing.SystemIcons.Application;
    }

    private static System.Windows.Media.ImageSource? LoadWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "jarvis.ico");
            if (!File.Exists(path)) return null;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void Quit()
    {
        _quitting = true;
        _tray?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloonTip(2500, "Jarvis AI",
                    "Jarvis reste actif en arrière-plan.\nCliquez sur l'icône pour rouvrir la fenêtre.",
                    ToolTipIcon.Info);
            }
        }
        base.OnClosing(e);
    }
}
