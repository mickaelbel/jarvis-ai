using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace JarvisAI.Infrastructure.Voice;

public interface ISystemTrayService
{
    void Enable();
    void Disable();
    void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info);
    void UpdateStatus(VoiceStatus status);
    event EventHandler? OnToggleVoice;
    event EventHandler? OnOpenSettings;
    event EventHandler? OnExit;
}

public sealed class SystemTrayService : ISystemTrayService, IDisposable
{
    private readonly ILogger<SystemTrayService> _logger;
    private NotifyIcon? _trayIcon;
    private bool _enabled;

    public event EventHandler? OnToggleVoice;
    public event EventHandler? OnOpenSettings;
    public event EventHandler? OnExit;

    public SystemTrayService(ILogger<SystemTrayService> logger)
    {
        _logger = logger;
    }

    public void Enable()
    {
        if (_enabled) return;

        try
        {
            _trayIcon = new NotifyIcon
            {
                Text = "Jarvis AI",
                Visible = true,
                Icon = GetJarvisIcon()
            };

            // Context menu
            var menu = new ContextMenuStrip();

            var voiceItem = new ToolStripMenuItem("Mode Vocal", null, (_, _) => OnToggleVoice?.Invoke(this, EventArgs.Empty));
            voiceItem.Checked = true;
            menu.Items.Add(voiceItem);

            var settingsItem = new ToolStripMenuItem("Paramètres", null, (_, _) => OnOpenSettings?.Invoke(this, EventArgs.Empty));
            menu.Items.Add(settingsItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Quitter", null, (_, _) => OnExit?.Invoke(this, EventArgs.Empty));
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = menu;

            _trayIcon.DoubleClick += (_, _) => OnOpenSettings?.Invoke(this, EventArgs.Empty);

            _enabled = true;
            _logger.LogInformation("[SystemTray] Enabled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SystemTray] Failed to enable");
        }
    }

    public void Disable()
    {
        if (!_enabled || _trayIcon is null) return;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
        _enabled = false;
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _trayIcon?.ShowBalloonTip(3000, title, message, icon);
    }

    public void UpdateStatus(VoiceStatus status)
    {
        if (_trayIcon is null) return;

        _trayIcon.Text = status switch
        {
            VoiceStatus.Listening => "Jarvis - Écoute...",
            VoiceStatus.Processing => "Jarvis - Réflexion...",
            VoiceStatus.Speaking => "Jarvis - Parle...",
            VoiceStatus.Idle => "Jarvis - Prêt",
            VoiceStatus.Disabled => "Jarvis - Désactivé",
            _ => "Jarvis AI"
        };

        _trayIcon.Icon = status switch
        {
            VoiceStatus.Listening => GetListeningIcon(),
            VoiceStatus.Speaking => GetSpeakingIcon(),
            _ => GetJarvisIcon()
        };
    }

    public void Dispose()
    {
        Disable();
    }

    private static Icon GetJarvisIcon()
    {
        // Create a simple "J" icon programmatically
        var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var font = new Font("Segoe UI", 16, FontStyle.Bold);
        graphics.DrawString("J", font, Brushes.RoyalBlue, 0, 0);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static Icon GetListeningIcon()
    {
        var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var font = new Font("Segoe UI", 14, FontStyle.Bold);
        graphics.DrawString("🎤", font, Brushes.Green, 2, 2);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static Icon GetSpeakingIcon()
    {
        var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var font = new Font("Segoe UI", 14, FontStyle.Bold);
        graphics.DrawString("🔊", font, Brushes.RoyalBlue, 2, 2);
        return Icon.FromHandle(bitmap.GetHicon());
    }
}

public enum VoiceStatus
{
    Idle,
    Listening,
    Processing,
    Speaking,
    Disabled
}
