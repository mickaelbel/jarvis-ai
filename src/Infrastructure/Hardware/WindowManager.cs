using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Hardware;

public interface IWindowManager
{
    IReadOnlyList<WindowInfo> GetWindows();
    void MoveWindow(IntPtr hWnd, int x, int y);
    void ResizeWindow(IntPtr hWnd, int width, int height);
    void MaximizeWindow(IntPtr hWnd);
    void MinimizeWindow(IntPtr hWnd);
    void RestoreWindow(IntPtr hWnd);
    void TileWindows(int columns, int rows);
    void ApplyRule(WindowRule rule);
    IReadOnlyList<WindowRule> GetRules();
    void SaveRules();
}

public sealed class WindowManager : IWindowManager
{
    private readonly ILogger<WindowManager> _logger;
    private readonly string _storagePath;
    private readonly List<WindowRule> _rules = new();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SW_MAXIMIZE = 3;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;

    public WindowManager(ILogger<WindowManager> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "window_rules.json");
        Load();
    }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            var title = sb.ToString();

            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowRect(hWnd, out var rect);
            windows.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                X = rect.Left,
                Y = rect.Top,
                Width = rect.Right - rect.Left,
                Height = rect.Bottom - rect.Top
            });

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public void MoveWindow(IntPtr hWnd, int x, int y)
    {
        SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
    }

    public void ResizeWindow(IntPtr hWnd, int width, int height)
    {
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, width, height, SWP_NOZORDER);
    }

    public void MaximizeWindow(IntPtr hWnd) => ShowWindow(hWnd, SW_MAXIMIZE);
    public void MinimizeWindow(IntPtr hWnd) => ShowWindow(hWnd, SW_MINIMIZE);
    public void RestoreWindow(IntPtr hWnd) => ShowWindow(hWnd, SW_RESTORE);

    public void TileWindows(int columns, int rows)
    {
        var windows = GetWindows().Take(columns * rows).ToList();
        var screen = SystemParameters.VirtualScreenWidth / columns;
        var screenHeight = SystemParameters.VirtualScreenHeight / rows;

        for (int i = 0; i < windows.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            MoveWindow(windows[i].Handle, col * screen, row * screenHeight);
            ResizeWindow(windows[i].Handle, screen, screenHeight);
        }

        _logger.LogInformation("[Window] Tiled {Count} windows ({Cols}x{Rows})", windows.Count, columns, rows);
    }

    public void ApplyRule(WindowRule rule)
    {
        var windows = GetWindows().Where(w =>
            w.Title.Contains(rule.TitleContains, StringComparison.OrdinalIgnoreCase));

        foreach (var window in windows)
        {
            if (rule.Maximize == true) MaximizeWindow(window.Handle);
            if (rule.Minimize == true) MinimizeWindow(window.Handle);
            if (rule.X.HasValue && rule.Y.HasValue) MoveWindow(window.Handle, rule.X.Value, rule.Y.Value);
            if (rule.Width.HasValue && rule.Height.HasValue) ResizeWindow(window.Handle, rule.Width.Value, rule.Height.Value);
        }
    }

    public IReadOnlyList<WindowRule> GetRules() => _rules.ToList();

    public void SaveRules()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_rules, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<WindowRule>>(json);
                if (loaded is not null) _rules.AddRange(loaded);
            }
        }
        catch { }
    }
}

public sealed class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class WindowRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TitleContains { get; set; } = "";
    public bool? Maximize { get; set; }
    public bool? Minimize { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool IsEnabled { get; set; } = true;
}

internal static class SystemParameters
{
    public static int VirtualScreenWidth => GetSystemMetrics(78);
    public static int VirtualScreenHeight => GetSystemMetrics(79);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
