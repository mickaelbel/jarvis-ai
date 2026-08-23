using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JarvisAI.Infrastructure.Windows;

/// <summary>Nom du processus au premier plan (pour le contexte des souvenirs).</summary>
public static class ForegroundAppProbe
{
    private static readonly ThreadLocal<DateTime> DernierAppel = new(() => DateTime.MinValue);
    private static string? _cache;
    private const int CacheMs = 2000;

    public static string? GetName()
    {
        try
        {
            if ((DateTime.UtcNow - DernierAppel.Value).TotalMilliseconds < CacheMs) return _cache;
            DernierAppel.Value = DateTime.UtcNow;

            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { _cache = null; return null; }
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) { _cache = null; return null; }
            using var p = Process.GetProcessById((int)pid);
            _cache = string.IsNullOrWhiteSpace(p.MainWindowTitle)
                ? p.ProcessName
                : $"{p.ProcessName} ({Truncate(p.MainWindowTitle, 60)})";
            return _cache;
        }
        catch
        {
            _cache = null;
            return null;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
