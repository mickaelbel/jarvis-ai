using Microsoft.Web.WebView2.Core;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;

namespace JarvisAI.Desktop;

public partial class MainWindow : Window
{
    private NotifyIcon? _tray;
    private bool _quitting;
    private bool _trayHintShown;
    private int _hotkeyId;
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        Icon = LoadWindowIcon();
        CreateTrayIcon();
        Loaded += (_, _) => RegisterShortcut();
        Closed += (_, _) => UnregisterShortcut();
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

    private void RegisterShortcut()
    {
        try
        {
            var shortcut = LoadDuckingShortcut();
            if (string.IsNullOrWhiteSpace(shortcut)) return;

            var (modifiers, vk) = ParseShortcut(shortcut);
            if (vk == 0) return;

            _hotkeyId = 9001;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            RegisterHotKey(hwnd, _hotkeyId, modifiers, vk);
        }
        catch { }
    }

    private void UnregisterShortcut()
    {
        try
        {
            if (_hotkeyId != 0)
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                UnregisterHotKey(hwnd, _hotkeyId);
            }
        }
        catch { }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RegisterShortcut();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProcHook);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _hotkeyId)
        {
            _ = ToggleDuckingAsync();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static string LoadDuckingShortcut()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", "voice-settings.json");
            if (!File.Exists(path)) return "Ctrl+Shift+D";
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("AudioDuckingShortcut", out var prop))
                return prop.GetString() ?? "Ctrl+Shift+D";
        }
        catch { }
        return "Ctrl+Shift+D";
    }

    private static async Task ToggleDuckingAsync()
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:51844") };
            await http.PostAsync("/api/voice/ducking/toggle",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        }
        catch { }
    }

    private static (uint modifiers, uint vk) ParseShortcut(string shortcut)
    {
        uint mod = 0;
        uint vk = 0;
        foreach (var part in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => "mod_ctrl",
                "shift" => "mod_shift",
                "alt" => "mod_alt",
                "win" or "meta" => "mod_win",
                _ => part
            };
            switch (p)
            {
                case "mod_ctrl": mod |= 0x0002; break;
                case "mod_shift": mod |= 0x0001; break;
                case "mod_alt": mod |= 0x0004; break;
                case "mod_win": mod |= 0x0008; break;
                default:
                    vk = part.Length == 1 ? (uint)char.ToUpper(part[0]) : MapVirtualKey(part);
                    break;
            }
        }
        return (mod, vk);
    }

    private static uint MapVirtualKey(string key) => key.ToLowerInvariant() switch
    {
        "f1" => 0x7A, "f2" => 0x7B, "f3" => 0x7C, "f4" => 0x7D,
        "f5" => 0x7E, "f6" => 0x7F, "f7" => 0x80, "f8" => 0x81,
        "f9" => 0x82, "f10" => 0x83, "f11" => 0x84, "f12" => 0x85,
        "space" => 0x20, "enter" or "return" => 0x0D, "tab" => 0x09,
        "escape" or "esc" => 0x1B, "backspace" => 0x08, "delete" => 0x2E,
        _ => 0
    };

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
