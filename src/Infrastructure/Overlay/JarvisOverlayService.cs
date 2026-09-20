using System.Collections.Concurrent;
using JarvisAI.Application.Overlay;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Overlay;

/// <summary>
/// Overlay service using a transparent WinForms window with GDI+ rendering.
/// Displays information on top of all applications with click-through support.
/// Iron Man style: cyan glow, dark background, smooth animations.
/// </summary>
public sealed class JarvisOverlayService : IOverlayService, IDisposable
{
    private readonly ILogger<JarvisOverlayService> _logger;
    private readonly ConcurrentDictionary<string, OverlayContent> _contents = new();
    private System.Windows.Forms.Form? _overlayForm;
    private System.Windows.Forms.Timer? _renderTimer;
    private System.Windows.Forms.Timer? _cleanupTimer;
    private readonly object _formLock = new();
    private bool _isRunning;

    // Iron Man colors
    private static readonly System.Drawing.Color CyanGlow = System.Drawing.Color.FromArgb(200, 0, 255, 255);
    private static readonly System.Drawing.Color DarkBg = System.Drawing.Color.FromArgb(180, 10, 15, 25);
    private static readonly System.Drawing.Color AlertRed = System.Drawing.Color.FromArgb(220, 255, 60, 60);
    private static readonly System.Drawing.Color SuccessGreen = System.Drawing.Color.FromArgb(220, 0, 255, 120);

    public bool IsRunning => _isRunning;

    public JarvisOverlayService(ILogger<JarvisOverlayService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return Task.CompletedTask;

        lock (_formLock)
        {
            try
            {
                _overlayForm = new System.Windows.Forms.Form
                {
                    Text = "Jarvis Overlay",
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    WindowState = System.Windows.Forms.FormWindowState.Normal,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    BackColor = System.Drawing.Color.Black,
                    TransparencyKey = System.Drawing.Color.Black,
                    TopMost = true,
                    ShowInTaskbar = false,
                    Size = new System.Drawing.Size(400, 600),
                    Location = new System.Drawing.Point(
                        System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width - 420,
                        60)
                };

                // Click-through via extended window style
                var exStyle = GetWindowLong(_overlayForm.Handle, GWL_EXSTYLE);
                SetWindowLong(_overlayForm.Handle, GWL_EXSTYLE,
                    exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);

                _overlayForm.Paint += OnPaint;
                _overlayForm.Show();

                // Render at 30 FPS
                _renderTimer = new System.Windows.Forms.Timer { Interval = 33 };
                _renderTimer.Tick += (s, e) => _overlayForm?.Invalidate();
                _renderTimer.Start();

                // Cleanup expired content every second
                _cleanupTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _cleanupTimer.Tick += (s, e) => CleanupExpired();
                _cleanupTimer.Start();

                _isRunning = true;
                _logger.LogInformation("[Overlay] Started on {Screen}",
                    System.Windows.Forms.Screen.PrimaryScreen.DeviceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Overlay] Failed to start");
                _isRunning = false;
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;

        lock (_formLock)
        {
            _renderTimer?.Stop();
            _renderTimer?.Dispose();
            _renderTimer = null;

            _cleanupTimer?.Stop();
            _cleanupTimer?.Dispose();
            _cleanupTimer = null;

            _overlayForm?.Close();
            _overlayForm?.Dispose();
            _overlayForm = null;

            _isRunning = false;
        }

        _logger.LogInformation("[Overlay] Stopped");
        return Task.CompletedTask;
    }

    public void Show(OverlayContent content)
    {
        _contents[content.Id] = content;
    }

    public void Toast(string message, string style = "default", int durationMs = 3000)
    {
        Show(new OverlayContent
        {
            Text = message,
            Style = style,
            DurationMs = durationMs,
            Position = "top-right"
        });
    }

    public void ShowSystemStats(float cpuPercent, float ramPercent, string? networkInfo = null)
    {
        var cpuBar = DrawBar(cpuPercent, 20);
        var ramBar = DrawBar(ramPercent, 20);
        var text = $"CPU {cpuPercent,5:F1}% {cpuBar}\nRAM {ramPercent,5:F1}% {ramBar}";
        if (!string.IsNullOrEmpty(networkInfo))
            text += $"\nNET {networkInfo}";

        Show(new OverlayContent
        {
            Id = "__system_stats__",
            Text = text,
            Style = "info",
            DurationMs = 0, // permanent
            Position = "bottom-right"
        });
    }

    public void Clear()
    {
        _contents.Clear();
    }

    private void OnPaint(object? sender, System.Windows.Forms.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var items = _contents.Values
            .OrderBy(c => c.Position)
            .ThenBy(c => c.CreatedAt)
            .ToList();

        float y = 10;
        int x = _overlayForm!.Width - 380;
        int maxWidth = 360;

        using var font = new System.Drawing.Font("Segoe UI", 11f, System.Drawing.FontStyle.Bold);
        using var smallFont = new System.Drawing.Font("Segoe UI", 9f);
        using var bgBrush = new System.Drawing.SolidBrush(DarkBg);
        using var cyanPen = new System.Drawing.Pen(CyanGlow, 2f);
        using var cyanBrush = new System.Drawing.SolidBrush(CyanGlow);

        foreach (var item in items)
        {
            var color = item.Style switch
            {
                "alert" => AlertRed,
                "success" => SuccessGreen,
                _ => CyanGlow
            };

            using var textBrush = new System.Drawing.SolidBrush(color);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(180, 10, 15, 25));

            var lines = item.Text.Split('\n');
            var lineHeight = font.GetHeight(g) + 4;
            var boxHeight = lines.Length * lineHeight + 16;

            // Background
            g.FillRectangle(bg, x - 5, y - 2, maxWidth + 10, boxHeight);

            // Border (left accent)
            g.DrawLine(new System.Drawing.Pen(color, 3f), x - 5, y, x - 5, y + boxHeight);

            // Text
            for (int i = 0; i < lines.Length; i++)
            {
                g.DrawString(lines[i], i == 0 ? font : smallFont, textBrush, x, y + 4 + i * lineHeight);
            }

            y += boxHeight + 8;
        }
    }

    private void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _contents)
        {
            if (kvp.Value.DurationMs > 0 &&
                (now - kvp.Value.CreatedAt).TotalMilliseconds > kvp.Value.DurationMs)
            {
                _contents.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string DrawBar(float percent, int width)
    {
        var filled = (int)(percent / 100 * width);
        return "[" + new string('=', filled) + new string('-', width - filled) + "]";
    }

    // Win32 constants
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
