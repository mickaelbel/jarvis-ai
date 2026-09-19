using JarvisAI.Application.ComputerUse;
using System.Collections.Concurrent;

namespace JarvisQARunner;

/// <summary>
/// Mock IComputerController that logs all actions without touching the real screen.
/// Used for headless QA testing.
/// </summary>
public sealed class HeadlessComputerController : IComputerController
{
    private long _nextHandle = 1000;
    private readonly ConcurrentBag<RecordedAction> _actions = new();
    private readonly Dictionary<long, string> _windows = new();
    private long _foregroundWindow;

    public bool IsAvailable => true;
    public IReadOnlyList<RecordedAction> Actions => _actions.ToArray();

    public void Reset()
    {
        while (!_actions.IsEmpty) _actions.TryTake(out _);
        _windows.Clear();
        _foregroundWindow = 0;
    }

    public Task<ScreenCapture?> CaptureScreenAsync(CancellationToken ct = default)
    {
        _actions.Add(new("capture_screen", "", DateTime.UtcNow));
        // Return a fake 1920x1080 screenshot
        var fakePng = new byte[100]; // minimal fake PNG
        return Task.FromResult<ScreenCapture?>(new(fakePng, 1920, 1080, 960, 540));
    }

    public Task<ScreenCapture?> CaptureWindowAsync(long handle, CancellationToken ct = default)
    {
        _actions.Add(new("capture_window", $"handle={handle}", DateTime.UtcNow));
        var fakePng = new byte[100];
        return Task.FromResult<ScreenCapture?>(new(fakePng, 800, 600, 400, 300));
    }

    public Task<bool> MoveMouseAsync(int x, int y, CancellationToken ct = default)
    {
        _actions.Add(new("move_mouse", $"({x},{y})", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken ct = default)
    {
        _actions.Add(new("click", $"{button} at ({x ?? 0},{y ?? 0})", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken ct = default)
    {
        _actions.Add(new("double_click", $"{button} at ({x ?? 0},{y ?? 0})", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> ScrollAsync(int deltaY, CancellationToken ct = default)
    {
        _actions.Add(new("scroll", $"delta={deltaY}", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> TypeTextAsync(string text, CancellationToken ct = default)
    {
        _actions.Add(new("type_text", text, DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> PressKeyAsync(string keyCombination, CancellationToken ct = default)
    {
        _actions.Add(new("press_key", keyCombination, DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct = default)
    {
        _actions.Add(new("list_windows", "", DateTime.UtcNow));
        // Return fake windows
        var windows = new List<WindowInfo>
        {
            new(1, "Jarvis AI", true, true, 0, 0, 1920, 1080),
            new(_nextHandle, "Notepad", true, false, 100, 100, 800, 600),
        };
        return Task.FromResult<IReadOnlyList<WindowInfo>>(windows);
    }

    public Task<bool> FocusWindowAsync(long handle, CancellationToken ct = default)
    {
        _actions.Add(new("focus_window", $"handle={handle}", DateTime.UtcNow));
        _foregroundWindow = handle;
        return Task.FromResult(true);
    }

    public Task<long> GetForegroundWindowAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_foregroundWindow);
    }

    public Task<string?> GetClipboardAsync(CancellationToken ct = default)
    {
        _actions.Add(new("get_clipboard", "", DateTime.UtcNow));
        return Task.FromResult<string?>("fake clipboard content");
    }

    public Task<bool> SetClipboardAsync(string text, CancellationToken ct = default)
    {
        _actions.Add(new("set_clipboard", text, DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> MinimizeWindowAsync(long handle, CancellationToken ct = default)
    {
        _actions.Add(new("minimize_window", $"handle={handle}", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> MaximizeWindowAsync(long handle, CancellationToken ct = default)
    {
        _actions.Add(new("maximize_window", $"handle={handle}", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> RestoreWindowAsync(long handle, CancellationToken ct = default)
    {
        _actions.Add(new("restore_window", $"handle={handle}", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> CloseWindowAsync(long handle, CancellationToken ct = default)
    {
        _actions.Add(new("close_window", $"handle={handle}", DateTime.UtcNow));
        _windows.Remove(handle);
        return Task.FromResult(true);
    }

    public Task<bool> MoveWindowAsync(long handle, int x, int y, CancellationToken ct = default)
    {
        _actions.Add(new("move_window", $"handle={handle} to ({x},{y})", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> ResizeWindowAsync(long handle, int width, int height, CancellationToken ct = default)
    {
        _actions.Add(new("resize_window", $"handle={handle} to {width}x{height}", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<WindowRect?> GetWindowRectAsync(long handle, CancellationToken ct = default)
    {
        return Task.FromResult<WindowRect?>(new(100, 100, 800, 600));
    }

    public Task<bool> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        _actions.Add(new("drag", $"({fromX},{fromY}) → ({toX},{toY})", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> HoverAsync(int x, int y, CancellationToken ct = default)
    {
        _actions.Add(new("hover", $"({x},{y})", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> MoveMouseSmoothAsync(int targetX, int targetY, int? steps = null, CancellationToken ct = default)
    {
        _actions.Add(new("move_mouse_smooth", $"({targetX},{targetY})", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> ClickBackgroundAsync(IntPtr hWnd, int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        _actions.Add(new("click_background", $"hWnd={hWnd} ({x},{y})", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> TypeBackgroundAsync(IntPtr hWnd, string text, CancellationToken ct = default)
    {
        _actions.Add(new("type_background", $"hWnd={hWnd} text='{text}'", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<bool> KeyBackgroundAsync(IntPtr hWnd, ushort vk, CancellationToken ct = default)
    {
        _actions.Add(new("key_background", $"hWnd={hWnd} vk={vk}", DateTime.UtcNow));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<MonitorInfo>> ListMonitorsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<MonitorInfo>>(new[] { new MonitorInfo(0, 0, 0, 1920, 1080, true) });
    }

    public Task<ScreenCapture?> CaptureMonitorAsync(int monitorIndex, CancellationToken ct = default)
    {
        _actions.Add(new("capture_monitor", $"index={monitorIndex}", DateTime.UtcNow));
        var fakePng = new byte[100];
        return Task.FromResult<ScreenCapture?>(new(fakePng, 1920, 1080, 960, 540));
    }
}

/// <summary>
/// Mock IComputerUseService that returns synthetic observations.
/// Used for headless QA testing.
/// </summary>
public sealed class HeadlessComputerUseService : IComputerUseService
{
    private readonly HeadlessComputerController _controller;

    public HeadlessComputerUseService(HeadlessComputerController controller)
    {
        _controller = controller;
    }

    public bool IsAvailable => true;

    public Task<UiObservation?> ObserveAsync(CancellationToken ct = default)
    {
        _controller.Actions.ToList(); // just to touch it
        // Return a synthetic observation
        return Task.FromResult<UiObservation?>(new UiObservation(
            ScreenWidth: 1920,
            ScreenHeight: 1080,
            CursorX: 960,
            CursorY: 540,
            OcrText: "Fake screen content for headless testing",
            Elements: Array.Empty<UiElement>(),
            Windows: new[]
            {
                new WindowInfo(1, "Jarvis AI", true, true, 0, 0, 1920, 1080),
                new WindowInfo(1000, "Notepad", true, false, 100, 100, 800, 600),
            },
            ImagePath: null));
    }

    public Task<UiElement?> FindElementAsync(string label, CancellationToken ct = default)
    {
        return Task.FromResult<UiElement?>(null);
    }

    public Task<UiActionResult> ClickElementAsync(string label, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        return Task.FromResult(new UiActionResult(false, null, $"Headless: cannot click '{label}'", 0, 0));
    }

    public Task<UiActionResult> DoubleClickElementAsync(string label, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        return Task.FromResult(new UiActionResult(false, null, $"Headless: cannot double-click '{label}'", 0, 0));
    }

    public Task<UiActionResult> TypeIntoElementAsync(string label, string text, CancellationToken ct = default)
    {
        return Task.FromResult(new UiActionResult(false, null, $"Headless: cannot type into '{label}'", 0, 0));
    }

    public Task<bool> WaitForUiStableAsync(int maxWaitMs = 2500, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }
}

public sealed record RecordedAction(string Type, string Details, DateTime Timestamp);

/// <summary>
/// Mock IUiElementDetector that returns empty results.
/// </summary>
public sealed class HeadlessUiElementDetector : IUiElementDetector
{
    public bool IsAvailable => true;
    public Task<IReadOnlyList<UiElement>> DetectAsync(byte[] imageBytes, int screenWidth, int screenHeight, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<UiElement>>(Array.Empty<UiElement>());
    }
}
