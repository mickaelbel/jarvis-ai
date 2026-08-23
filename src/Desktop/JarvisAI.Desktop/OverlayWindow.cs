using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace JarvisAI.Desktop;

/// <summary>
/// Mini-fenêtre flottante qui affiche à l'écrit ce que Jarvis répond.
/// Règle d'or : ne vole JAMAIS le focus (WS_EX_NOACTIVATE), reste au-dessus
/// (topmost), est invisible pour OBS/captures (WDA_EXCLUDEFROMCAPTURE) et est
/// cliquable uniquement quand la souris la survole (clic-transparent sinon).
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private readonly TextBlock _textBlock;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _hoverTimer;
    private bool _pinned;
    private IntPtr _handle;

    public OverlayWindow()
    {
        Title = "Jarvis";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 24, 26, 33));
        Width = 460;
        Height = double.NaN; // auto sur le contenu
        SizeToContent = SizeToContent.Height;
        Opacity = 0.96;

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = (_textBlock = new TextBlock
                {
                    Foreground = System.Windows.Media.Brushes.WhiteSmoke,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = double.NaN
                })
            }
        };
        Content = border;

        MouseLeftButtonDown += (_, _) => { _pinned = !_pinned; if (!_pinned) { _hideTimer.Stop(); _hideTimer.Start(); } };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideOverlay(); };

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverTimer.Tick += CheckHover;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;

        int style = GetWindowLong(_handle, GWL_EXSTYLE);
        SetWindowLong(_handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_TRANSPARENT);
        SetWindowDisplayAffinity(_handle, WDA_EXCLUDEFROMCAPTURE);

        PositionOnSecondScreen();
        _hoverTimer.Start();
    }

    /// <summary>Affiche un message ; durée auto selon la longueur (4–14 s).</summary>
    public void ShowMessage(string message, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.Invoke(() =>
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.AppendLine(title);
            sb.Append(message.Trim());
            _textBlock.Text = sb.ToString();

            PositionOnSecondScreen();
            Show(); // n'active jamais grâce à WS_EX_NOACTIVATE
            InteropHelp.ActivateNoFocus(this);

            int words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var duration = TimeSpan.FromSeconds(Math.Clamp(4 + words * 0.35, 4, 14));
            _hideTimer.Stop();
            _hideTimer.Interval = duration;
            _hideTimer.Start();
        });
    }

    public void HideOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            _pinned = false;
            try { Hide(); } catch { }
        });
    }

    /// <summary>Clic-transparent sauf quand la souris survole la fenêtre.</summary>
    private void CheckHover(object? sender, EventArgs e)
    {
        if (_handle == IntPtr.Zero || !IsVisible) return;
        try
        {
            GetCursorPos(out var pt);
            var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            // Coordonnées fenêtre en pixels écran :
            if (pt.X >= Left && pt.X <= Left + ActualWidth && pt.Y >= Top && pt.Y <= Top + ActualHeight)
            {
                int style = GetWindowLong(_handle, GWL_EXSTYLE);
                SetWindowLong(_handle, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT); // cliquable
            }
            else
            {
                int style = GetWindowLong(_handle, GWL_EXSTYLE);
                SetWindowLong(_handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT); // traversable
            }
        }
        catch
        {
        }
    }

    /// <summary>Place l'overlay en bas à droite de l'écran secondaire s'il existe, sinon du principal.</summary>
    private void PositionOnSecondScreen()
    {
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var screen = screens.Length > 1 ? screens[1] : screens[0];
            var wa = screen.WorkingArea;
            double dpiScale = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
                dpiScale = source.CompositionTarget.TransformToDevice.M11;

            Left = (wa.Right - (Width * dpiScale) - 24) / dpiScale;
            Top = (wa.Bottom - (ActualHeight > 0 ? ActualHeight : 120) * dpiScale - 48) / dpiScale;
        }
        catch
        {
            Left = 40;
            Top = 40;
        }
    }
}

internal static class InteropHelp
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static void ActivateNoFocus(Window window)
    {
        // ShowActivated=false suffit ; méthode de sécurité si une policy réactive.
        // Ne met PAS le focus volontairement.
    }
}
