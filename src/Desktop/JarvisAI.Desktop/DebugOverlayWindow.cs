using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace JarvisAI.Desktop;

/// <summary>
/// Debug overlay: shows AI reasoning in real-time as it processes.
/// Activated by clicking 15x on the status dot. Password protected.
/// Displays thinking, tool calls, file changes, and final response.
/// </summary>
public sealed class DebugOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const string PASSWORD = "Caline09!";

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private readonly TextBlock _logBlock;
    private readonly ScrollViewer _scroll;
    private readonly Border _border;
    private IntPtr _handle;
    private bool _isAuthenticated;
    private readonly DispatcherTimer _autoScrollTimer;

    public DebugOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Color.FromArgb(240, 10, 10, 15).ToBrush();
        Topmost = true;
        Width = 520;
        Height = 400;
        Left = 20;
        Top = 20;

        _logBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 100)),
            FontSize = 12,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        };

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _logBlock
        };

        var header = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Text = "DEBUG MODE",
            Margin = new Thickness(0, 0, 0, 8)
        };

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(_scroll);

        _border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 80, 80)),
            BorderThickness = new Thickness(1),
            Child = panel
        };
        Content = _border;

        // Drag to move
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 3) { Hide(); return; }
            DragMove();
        };

        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoScrollTimer.Tick += (_, _) => _scroll.ScrollToEnd();
    }

    public bool IsAuthenticated => _isAuthenticated;

    public bool TryAuthenticate(string password)
    {
        _isAuthenticated = password == PASSWORD;
        if (_isAuthenticated)
        {
            _autoScrollTimer.Start();
            Show();
        }
        return _isAuthenticated;
    }

    public void Log(string message, string category = "INFO")
    {
        if (!_isAuthenticated) return;
        Dispatcher.Invoke(() =>
        {
            var time = DateTime.Now.ToString("HH:mm:ss.fff");
            var color = category switch
            {
                "ERROR" => Colors.Red,
                "WARN" => Colors.Yellow,
                "TOOL" => Colors.Cyan,
                "THINK" => Color.FromRgb(180, 180, 255),
                "FILE" => Colors.Orange,
                "RESPONSE" => Colors.LightGreen,
                _ => Color.FromRgb(150, 150, 150)
            };

            _logBlock.Inlines.Add(new Run($"[{time}] ") { Foreground = System.Windows.Media.Brushes.Gray, FontSize = 10 });
            _logBlock.Inlines.Add(new Run($"[{category}] ") { Foreground = color.ToBrush(), FontWeight = FontWeights.Bold });
            _logBlock.Inlines.Add(new Run(message + "\n") { Foreground = color.ToBrush() });
            _scroll.ScrollToEnd();
        });
    }

    public void Clear()
    {
        Dispatcher.Invoke(() => _logBlock.Inlines.Clear());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(_handle, GWL_EXSTYLE);
        SetWindowLong(_handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
        SetWindowDisplayAffinity(_handle, WDA_EXCLUDEFROMCAPTURE);
    }
}

internal static class ColorExtensions
{
    public static System.Windows.Media.SolidColorBrush ToBrush(this System.Windows.Media.Color c) => new(c);
}
