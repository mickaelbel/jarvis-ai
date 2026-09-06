using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace JarvisAI.Desktop;

/// <summary>
/// Overlay image: no window chrome, no taskbar icon, click-through except on content.
/// Drag to move, triple-click to close. Supports slideshow mode.
/// </summary>
public sealed class ImageOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private readonly System.Windows.Controls.Image _image;
    private readonly System.Windows.Controls.MediaElement _mediaElement;
    private readonly Border _border;
    private IntPtr _handle;
    private bool _isDragging;
    private Point _dragStart;
    private int _clickCount;
    private DateTime _lastClickTime = DateTime.MinValue;
    private readonly DispatcherTimer _clickResetTimer;
    private readonly DispatcherTimer _slideshowTimer;
    private string[]? _slideshowPaths;
    private int _slideshowIndex;
    private readonly Action? _onClose;

    public ImageOverlayWindow(Action? onClose = null)
    {
        _onClose = onClose;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 800;
        MaxHeight = 600;

        _image = new System.Windows.Controls.Image
        {
            RenderTransform = new ScaleTransform(1, 1),
            ClipToBounds = true,
            MaxWidth = 800,
            MaxHeight = 600
        };

        _mediaElement = new System.Windows.Controls.MediaElement
        {
            LoadedBehavior = System.Windows.Controls.MediaState.Play,
            UnloadedBehavior = System.Windows.Controls.MediaState.Stop,
            IsMuted = false,
            ClipToBounds = true,
            MaxWidth = 800,
            MaxHeight = 600,
            Visibility = Visibility.Collapsed
        };

        var panel = new Grid();
        panel.Children.Add(_image);
        panel.Children.Add(_mediaElement);

        _border = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 270,
                ShadowDepth = 4,
                BlurRadius = 16,
                Opacity = 0.5
            },
            Child = panel
        };
        Content = _border;

        // Triple-click to close
        _clickResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _clickResetTimer.Tick += (_, _) => { _clickCount = 0; _clickResetTimer.Stop(); };

        // Slideshow timer
        _slideshowTimer = new DispatcherTimer();
        _slideshowTimer.Tick += SlideshowTick;

        MouseLeftButtonDown += OnMouseLeftDown;
        MouseLeftButtonUp += OnMouseLeftUp;
        MouseMove += OnMouseMove;
    }

    private void OnMouseLeftDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastClickTime).TotalMilliseconds < 400)
            _clickCount++;
        else
            _clickCount = 1;
        _lastClickTime = now;

        if (_clickCount >= 3)
        {
            CloseOverlay();
            return;
        }

        _clickResetTimer.Stop();
        _clickResetTimer.Start();

        _isDragging = true;
        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseLeftUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDragging = false;
        ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        Left += pos.X - _dragStart.X;
        Top += pos.Y - _dragStart.Y;
    }

    /// <summary>Show a single image at optional position.</summary>
    public void ShowImage(string path, double? x = null, double? y = null, double? width = null)
    {
        if (!System.IO.File.Exists(path)) return;

        _mediaElement.Visibility = Visibility.Collapsed;
        _image.Visibility = Visibility.Visible;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();

        _image.Source = bitmap;
        if (width.HasValue)
        {
            _image.Width = width.Value;
            _image.Height = width.Value * (bitmap.PixelHeight / (double)bitmap.PixelWidth);
        }

        if (x.HasValue) Left = x.Value;
        if (y.HasValue) Top = y.Value;

        Show();
    }

    /// <summary>Show a video at optional position.</summary>
    public void ShowVideo(string path, double? x = null, double? y = null, double? width = null)
    {
        if (!System.IO.File.Exists(path)) return;

        _image.Visibility = Visibility.Collapsed;
        _mediaElement.Visibility = Visibility.Visible;

        _mediaElement.Source = new Uri(path, UriKind.Absolute);
        if (width.HasValue)
        {
            _mediaElement.Width = width.Value;
            _mediaElement.Height = width.Value * 9.0 / 16.0;
        }

        if (x.HasValue) Left = x.Value;
        if (y.HasValue) Top = y.Value;

        Show();
    }

    /// <summary>Start a slideshow of images.</summary>
    public void StartSlideshow(string[] imagePaths, int intervalMs = 3000, double? x = null, double? y = null)
    {
        if (imagePaths.Length == 0) return;
        _slideshowPaths = imagePaths;
        _slideshowIndex = 0;
        _slideshowTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        _slideshowTimer.Start();

        if (x.HasValue) Left = x.Value;
        if (y.HasValue) Top = y.Value;

        ShowImage(imagePaths[0]);
    }

    private void SlideshowTick(object? sender, EventArgs e)
    {
        if (_slideshowPaths is null || _slideshowPaths.Length == 0) return;
        _slideshowIndex = (_slideshowIndex + 1) % _slideshowPaths.Length;
        ShowImage(_slideshowPaths[_slideshowIndex]);
    }

    public void StopSlideshow()
    {
        _slideshowTimer.Stop();
        _slideshowPaths = null;
    }

    private void CloseOverlay()
    {
        StopSlideshow();
        Hide();
        _onClose?.Invoke();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(_handle, GWL_EXSTYLE);
        SetWindowLong(_handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_TRANSPARENT);
        SetWindowDisplayAffinity(_handle, WDA_EXCLUDEFROMCAPTURE);
    }
}
