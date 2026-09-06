namespace JarvisAI.Application.ComputerUse;

public enum MouseButton
{
    Left,
    Right,
    Middle
}

public sealed record ScreenCapture(byte[] PngBytes, int Width, int Height, int CursorX, int CursorY);

public sealed record WindowInfo(
    long Handle,
    string Title,
    bool IsVisible,
    bool IsFocused,
    int X = 0,
    int Y = 0,
    int Width = 0,
    int Height = 0);

public sealed record WindowRect(int X, int Y, int Width, int Height);

public interface IComputerController
{
    bool IsAvailable { get; }
    Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default);
    Task<bool> MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default);
    Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default);
    Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default);
    Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default);
    Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default);
    Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default);
    Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default);
    Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default);
    Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default);

    Task<long> GetForegroundWindowAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    Task<bool> MinimizeWindowAsync(long handle, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> MaximizeWindowAsync(long handle, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> RestoreWindowAsync(long handle, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> CloseWindowAsync(long handle, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> MoveWindowAsync(long handle, int x, int y, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> ResizeWindowAsync(long handle, int width, int height, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<WindowRect?> GetWindowRectAsync(long handle, CancellationToken cancellationToken = default)
        => Task.FromResult<WindowRect?>(null);

    Task<bool> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> HoverAsync(int x, int y, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> MoveMouseSmoothAsync(int targetX, int targetY, int? steps = null, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> ClickBackgroundAsync(IntPtr hWnd, int x, int y, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> TypeBackgroundAsync(IntPtr hWnd, string text, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<bool> KeyBackgroundAsync(IntPtr hWnd, ushort vk, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    Task<IReadOnlyList<MonitorInfo>> ListMonitorsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MonitorInfo>>(Array.Empty<MonitorInfo>());

    Task<ScreenCapture?> CaptureMonitorAsync(int monitorIndex, CancellationToken cancellationToken = default)
        => Task.FromResult<ScreenCapture?>(null);
}

public sealed record MonitorInfo(int Index, int X, int Y, int Width, int Height, bool IsPrimary);
