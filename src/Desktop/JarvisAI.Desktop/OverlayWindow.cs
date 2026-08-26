using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private readonly TextBlock _statusText;
    private readonly System.Windows.Shapes.Ellipse _statusDot;
    private readonly ScrollViewer _scroll;
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

        (_statusDot, _statusText) = BuildStatusBar();

        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(_statusDot);
        header.Children.Add(_statusText);

        // Waveform vivante : 28 barres alignées après le label d'état.
        var wavePanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        _waveBars = new System.Windows.Shapes.Rectangle[_wave.Length];
        for (var i = 0; i < _wave.Length; i++)
        {
            _waveBars[i] = new System.Windows.Shapes.Rectangle
            {
                Width = 3,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(86, 204, 242)),
                Opacity = 0.35,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            wavePanel.Children.Add(_waveBars[i]);
        }
        header.Children.Add(wavePanel);

        _scroll = new ScrollViewer
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
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children = { header, _scroll }
            }
        };
        Content = border;

        MouseLeftButtonDown += (_, _) => { _pinned = !_pinned; if (!_pinned && _hideTimer is not null) { _hideTimer.Stop(); _hideTimer.Start(); } };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideOverlay(); };

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverTimer.Tick += CheckHover;
    }

    // Waveform : 28 barres glissantes alimentées par le niveau micro live.
    private readonly double[] _wave = new double[28];
    private int _waveIndex;
    private System.Windows.Shapes.Rectangle[]? _waveBars;

    /// <summary>Niveau micro reçu du serveur (HUD waveform).</summary>
    public void SetLevel(double rms)
    {
        Dispatcher.Invoke(() =>
        {
            if (_waveBars is null) return;
            // ×25 : visible dès le souffle d'un micro faible gain (~0,005 RMS,
            // casque BT) tout en saturant pour une voix normale (~0,03).
            _wave[_waveIndex] = Math.Clamp(rms * 25.0, 0.02, 1.0);
            _waveIndex = (_waveIndex + 1) % _wave.Length;
            for (var i = 0; i < _wave.Length; i++)
            {
                var v = _wave[(_waveIndex + i) % _wave.Length];
                _waveBars[i].Height = 3 + v * 26;
                _waveBars[i].Opacity = 0.35 + v * 0.65;
            }
        });
    }

    private static (System.Windows.Shapes.Ellipse dot, TextBlock label) BuildStatusBar()
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 124, 135)),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = "prêt",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 174, 186)),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        return (dot, label);
    }

    private void SetStatusVisual(byte r, byte g, byte b, string label)
    {
        _statusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        _statusText.Text = label;
    }

    /// <summary>État vocal temps réel reçu du serveur (HUD).</summary>
    public void SetStatus(string state)
    {
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case "Listening":
                    SetStatusVisual(86, 204, 242, "je vous écoute…");
                    break;
                case "Processing":
                    SetStatusVisual(241, 196, 15, "je réfléchis…");
                    break;
                case "Speaking":
                    SetStatusVisual(46, 204, 113, "je parle");
                    break;
                case "Error":
                    SetStatusVisual(231, 76, 60, "erreur");
                    break;
                default:
                    SetStatusVisual(120, 124, 135, "prêt");
                    break;
            }
            if (!IsVisible && state != "Idle") Show();
        });
    }

    /// <summary>Un outil vient de s'exécuter → flash dans la barre d'état.</summary>
    public void SetToolActivity(string toolName, bool success)
    {
        Dispatcher.Invoke(() =>
        {
            SetStatusVisual(
                success ? (byte)155 : (byte)231,
                success ? (byte)89 : (byte)76,
                success ? (byte)182 : (byte)60,
                $"⚙ {toolName}");
            if (!IsVisible) Show();
        });
    }

    /// <summary>Ce que l'utilisateur est en train de dire (STT partiel, HUD).
    /// Le mot de réveil « Jarvis » reconnu dans la phrase est affiché en vert.</summary>
    public void ShowUserPartial(string texte)
    {
        Dispatcher.Invoke(() =>
        {
            _textBlock.Inlines.Clear();
            _textBlock.Inlines.Add(new Run("vous : ") { Foreground = System.Windows.Media.Brushes.Gray });
            AppendAvecReveilEnVert(_textBlock, texte ?? "");
            _textBlock.Opacity = 0.75;
            _scroll.ScrollToEnd();
            if (!IsVisible) Show();
        });
    }

    private static readonly System.Text.RegularExpressions.Regex _wakeRegex =
        new(@"(jarv\w*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Ajoute le texte au bloc en mettant chaque occurrence du
    /// mot-clé (« jarvis », « jarvi »…) en vert semi-gras.</summary>
    private static void AppendAvecReveilEnVert(System.Windows.Controls.TextBlock bloc, string texte)
    {
        var index = 0;
        foreach (System.Text.RegularExpressions.Match m in _wakeRegex.Matches(texte))
        {
            if (m.Index > index)
                bloc.Inlines.Add(new Run(texte[index..m.Index]));
            bloc.Inlines.Add(new Run(m.Value)
            {
                Foreground = System.Windows.Media.Brushes.MediumSpringGreen,
                FontWeight = System.Windows.FontWeights.Bold
            });
            index = m.Index + m.Length;
        }
        if (index < texte.Length)
            bloc.Inlines.Add(new Run(texte[index..]));
    }

    /// <summary>Aperçu du texte pendant que le LLM écrit (mode HUD live).</summary>
    public void ShowStreaming(string partialText)
    {
        if (string.IsNullOrWhiteSpace(partialText)) return;
        Dispatcher.Invoke(() =>
        {
            var clean = partialText.Trim();
            // Longue réponse : on garde tout et le TextBlock passe à la ligne
            // (pas de troncature « … » — l'utilisateur veut lire en continu).
            _textBlock.Text = clean;
            _textBlock.Opacity = 1; // efface le style "vous : …" du partiel
            _scroll.ScrollToEnd();
            PositionOnSecondScreen();
            if (!IsVisible) Show();
            SetStatusVisual(241, 196, 15, "j'écris…");
        });
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
        // Miroir natif Windows (utile quand l'overlay est masqué / autre écran).
        Task.Run(() => Toast.Show(string.IsNullOrWhiteSpace(title) ? "Jarvis" : title, message));
        Dispatcher.Invoke(() =>
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.AppendLine(title);
            sb.Append(message.Trim());
            _textBlock.Text = sb.ToString();

            PositionOnSecondScreen();
            Show(); // n'active jamais grâce à WS_EX_NOACTIVATE
            InteropHelp.ActivateNoFocus(this);
            SetStatusVisual(46, 204, 113, "je parle");

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
