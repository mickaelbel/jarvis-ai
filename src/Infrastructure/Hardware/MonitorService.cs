using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Hardware;

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    MonitorInfo GetPrimaryMonitor();
    MonitorInfo? GetMonitorFromPoint(int x, int y);
    void MoveWindowToMonitor(IntPtr hWnd, int monitorIndex);
    int GetMonitorCount();
    MonitorInfo GetBestMonitorForWindow(IntPtr hWnd);
}

public sealed class MonitorService : IMonitorService
{
    private readonly ILogger<MonitorService> _logger;

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;

    public MonitorService(ILogger<MonitorService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new MonitorInfo
                {
                    Handle = hMonitor,
                    Name = info.szDevice,
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    WorkLeft = info.rcWork.Left,
                    WorkTop = info.rcWork.Top,
                    WorkWidth = info.rcWork.Right - info.rcWork.Left,
                    WorkHeight = info.rcWork.Bottom - info.rcWork.Top,
                    IsPrimary = (info.dwFlags & 1) != 0
                });
            }
            return true;
        }, IntPtr.Zero);

        _logger.LogDebug("[Monitor] Detected {Count} monitors", monitors.Count);
        return monitors;
    }

    public MonitorInfo GetPrimaryMonitor()
    {
        return GetMonitors().FirstOrDefault(m => m.IsPrimary)
            ?? GetMonitors().FirstOrDefault()
            ?? new MonitorInfo { Name = "Primary", Width = 1920, Height = 1080 };
    }

    public MonitorInfo? GetMonitorFromPoint(int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        var hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        return GetMonitors().FirstOrDefault(m => m.Handle == hMonitor);
    }

    public void MoveWindowToMonitor(IntPtr hWnd, int monitorIndex)
    {
        var monitors = GetMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count) return;

        var target = monitors[monitorIndex];
        SetWindowPos(hWnd, IntPtr.Zero,
            target.WorkLeft + 50,
            target.WorkTop + 50,
            0, 0, SWP_NOSIZE | SWP_NOZORDER);

        _logger.LogInformation("[Monitor] Moved window to monitor {Index} ({Name})",
            monitorIndex, target.Name);
    }

    public int GetMonitorCount() => GetMonitors().Count;

    public MonitorInfo GetBestMonitorForWindow(IntPtr hWnd)
    {
        var hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        return GetMonitors().FirstOrDefault(m => m.Handle == hMonitor) ?? GetPrimaryMonitor();
    }
}

public sealed class MonitorInfo
{
    public IntPtr Handle { get; set; }
    public string Name { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WorkLeft { get; set; }
    public int WorkTop { get; set; }
    public int WorkWidth { get; set; }
    public int WorkHeight { get; set; }
    public bool IsPrimary { get; set; }
    public double DpiScale => Width > 1920 ? 1.5 : 1.0;
}
