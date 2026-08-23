using JarvisAI.Application.ComputerUse;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace JarvisAI.Infrastructure.ComputerUse;

public sealed class WindowsComputerController : IComputerController
{
    private readonly ILogger<WindowsComputerController> _logger;

    public WindowsComputerController(ILogger<WindowsComputerController> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public async Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("[ComputerUse] Screen capture is only available on Windows");
            return null;
        }

        var width = GetSystemMetrics(SM_CXSCREEN);
        var height = GetSystemMetrics(SM_CYSCREEN);
        if (width <= 0 || height <= 0)
            return null;

        var png = await Task.Run(() => CapturePng(width, height), cancellationToken);
        if (png is null)
            return null;

        GetCursorPos(out var point);
        _logger.LogInformation("[ComputerUse] Captured screen {W}x{H}, cursor at ({X},{Y})", width, height, point.X, point.Y);
        return new ScreenCapture(png, width, height, point.X, point.Y);
    }

    public Task<bool> MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        var moved = SetCursorPos(x, y);
        if (moved)
            _logger.LogDebug("[ComputerUse] Mouse moved to ({X},{Y})", x, y);
        else
            _logger.LogWarning("[ComputerUse] Failed to move mouse to ({X},{Y})", x, y);
        return Task.FromResult(moved);
    }

    public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        if (x.HasValue && y.HasValue)
            SetCursorPos(x.Value, y.Value);

        var (down, up) = button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        SendMouseEvent(down);
        Thread.Sleep(30);
        SendMouseEvent(up);

        _logger.LogDebug("[ComputerUse] Clicked {Button} at ({X},{Y})", button, x, y);
        return Task.FromResult(true);
    }

    public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        if (x.HasValue && y.HasValue)
            SetCursorPos(x.Value, y.Value);

        var (down, up) = button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        for (var i = 0; i < 2; i++)
        {
            SendMouseEvent(down);
            Thread.Sleep(30);
            SendMouseEvent(up);
            Thread.Sleep(50);
        }

        _logger.LogDebug("[ComputerUse] Double-clicked {Button} at ({X},{Y})", button, x, y);
        return Task.FromResult(true);
    }

    public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        // MOUSEEVENTF_WHEEL expects the signed delta in the HIGH word of
        // mouseData; the low word is ignored by the system.
        SendMouseEvent(MOUSEEVENTF_WHEEL, mouseData: (uint)(deltaY << 16));
        _logger.LogDebug("[ComputerUse] Scrolled by {Delta}", deltaY);
        return Task.FromResult(true);
    }

    public async Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (string.IsNullOrEmpty(text))
            return true;

        // Paced input: foreground apps (and input-virtualization layers) can
        // drop or reorder characters when a large burst is injected at once.
        foreach (var c in text)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (c == '\r')
                continue;

            var inputs = new List<INPUT>(4);
            if (c == '\n')
            {
                AddKeyInputs(inputs, VK_RETURN, 0, KEYEVENTF_KEYUP);
            }
            else
            {
                inputs.Add(CreateKeyInput(VK_UNDEFINED, c, 0, KEYEVENTF_UNICODE));
                inputs.Add(CreateKeyInput(VK_UNDEFINED, c, 0, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }

            SendInputs(inputs);
            await Task.Delay(50, cancellationToken);
        }

        _logger.LogDebug("[ComputerUse] Typed {Length} characters", text.Length);
        return true;
    }

    public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        if (string.IsNullOrWhiteSpace(keyCombination))
            return Task.FromResult(false);

        var parts = keyCombination.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return Task.FromResult(false);

        var modifiers = new List<ushort>();
        ushort mainKey = 0;
        foreach (var part in parts)
        {
            if (part == "ctrl" || part == "control")
                modifiers.Add(VK_CONTROL);
            else if (part == "alt")
                modifiers.Add(VK_MENU);
            else if (part == "shift")
                modifiers.Add(VK_SHIFT);
            else if (part == "win")
                modifiers.Add(VK_LWIN);
            else
                mainKey = MapKeyCode(part);
        }

        if (mainKey == 0)
            return Task.FromResult(false);

        var inputs = new List<INPUT>();
        foreach (var mod in modifiers)
            inputs.Add(CreateKeyInput(mod, 0, 0, 0));
        inputs.Add(CreateKeyInput(mainKey, 0, 0, 0));
        inputs.Add(CreateKeyInput(mainKey, 0, 0, KEYEVENTF_KEYUP));
        for (var i = modifiers.Count - 1; i >= 0; i--)
            inputs.Add(CreateKeyInput(modifiers[i], 0, 0, KEYEVENTF_KEYUP));

        SendInputs(inputs);
        _logger.LogDebug("[ComputerUse] Pressed key combination: {Combination}", keyCombination);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());

        var windows = new List<WindowInfo>();
        var foreground = GetForegroundWindow();

        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd))
            {
                var title = GetWindowTitle(hWnd);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var x = 0;
                    var y = 0;
                    var width = 0;
                    var height = 0;

                    if (GetWindowRect(hWnd, out var rect))
                    {
                        x = rect.Left;
                        y = rect.Top;
                        width = Math.Max(0, rect.Right - rect.Left);
                        height = Math.Max(0, rect.Bottom - rect.Top);
                    }

                    windows.Add(new WindowInfo(hWnd.ToInt64(), title, true, hWnd == foreground, x, y, width, height));
                }
            }
            return true;
        }, IntPtr.Zero);

        return Task.FromResult<IReadOnlyList<WindowInfo>>(windows);
    }

    public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        var hWnd = new IntPtr(handle);
        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        var foreground = GetForegroundWindow();
        if (foreground != hWnd)
        {
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var targetThread = GetWindowThreadProcessId(hWnd, out _);
            var currentThread = GetCurrentThreadId();

            // Windows refuses SetForegroundWindow for background processes.
            // Attach our input queue to both threads and simulate a harmless
            // ALT key press to release the foreground lock.
            var attached = false;
            if (foregroundThread != 0 && targetThread != 0 && currentThread != 0 &&
                foregroundThread != currentThread && targetThread != currentThread)
            {
                attached = AttachThreadInput(currentThread, targetThread, true) &&
                           AttachThreadInput(currentThread, foregroundThread, true);
            }

            try
            {
                keybd_event((byte)VK_MENU, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                keybd_event((byte)VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);

                BringWindowToTop(hWnd);
                ShowWindow(hWnd, SW_SHOW);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                    AttachThreadInput(currentThread, targetThread, false);
                }
            }
        }

        var focused = GetForegroundWindow() == hWnd;
        _logger.LogDebug("[ComputerUse] Focusing window handle {Handle}: {Result}", handle, focused);
        return Task.FromResult(focused);
    }

    public Task<long> GetForegroundWindowAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(0L);

        var hWnd = GetForegroundWindow();
        return Task.FromResult(hWnd == IntPtr.Zero ? 0L : hWnd.ToInt64());
    }

    public Task<bool> MinimizeWindowAsync(long handle, CancellationToken cancellationToken = default)
        => ShowWindowStateAsync(handle, SW_MINIMIZE, "minimize");

    public Task<bool> MaximizeWindowAsync(long handle, CancellationToken cancellationToken = default)
        => ShowWindowStateAsync(handle, SW_MAXIMIZE, "maximize");

    public Task<bool> RestoreWindowAsync(long handle, CancellationToken cancellationToken = default)
        => ShowWindowStateAsync(handle, SW_RESTORE, "restore");

    private Task<bool> ShowWindowStateAsync(long handle, int showCommand, string operation)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        var hWnd = new IntPtr(handle);
        var ok = ShowWindow(hWnd, showCommand);
        _logger.LogDebug("[ComputerUse] {Operation} window handle {Handle}: {Result}", operation, handle, ok);
        return Task.FromResult(true);
    }

    public Task<bool> CloseWindowAsync(long handle, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        var hWnd = new IntPtr(handle);
        var ok = PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _logger.LogDebug("[ComputerUse] Close window handle {Handle}: {Result}", handle, ok);
        return Task.FromResult(ok);
    }

    public async Task<bool> MoveWindowAsync(long handle, int x, int y, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var rect = await GetWindowRectAsync(handle, cancellationToken);
        if (rect is null)
            return false;

        var ok = MoveWindow(new IntPtr(handle), x, y, rect.Width, rect.Height, true);
        _logger.LogDebug("[ComputerUse] Move window handle {Handle} to ({X},{Y}): {Result}", handle, x, y, ok);
        return ok;
    }

    public async Task<bool> ResizeWindowAsync(long handle, int width, int height, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var rect = await GetWindowRectAsync(handle, cancellationToken);
        if (rect is null)
            return false;

        var ok = MoveWindow(new IntPtr(handle), rect.X, rect.Y, width, height, true);
        _logger.LogDebug("[ComputerUse] Resize window handle {Handle} to {W}x{H}: {Result}", handle, width, height, ok);
        return ok;
    }

    public Task<WindowRect?> GetWindowRectAsync(long handle, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<WindowRect?>(null);

        if (!GetWindowRect(new IntPtr(handle), out var rect))
            return Task.FromResult<WindowRect?>(null);

        return Task.FromResult<WindowRect?>(new WindowRect(
            rect.Left,
            rect.Top,
            Math.Max(0, rect.Right - rect.Left),
            Math.Max(0, rect.Bottom - rect.Top)));
    }

    public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<string?>(null);

        string? result = null;
        if (OpenClipboard(IntPtr.Zero))
        {
            try
            {
                var handle = GetClipboardData(CF_UNICODETEXT);
                if (handle != IntPtr.Zero)
                {
                    var ptr = GlobalLock(handle);
                    if (ptr != IntPtr.Zero)
                    {
                        try
                        {
                            result = Marshal.PtrToStringUni(ptr);
                        }
                        finally
                        {
                            GlobalUnlock(handle);
                        }
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        return Task.FromResult(result);
    }

    public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        if (text is null)
            return Task.FromResult(false);

        var byteCount = (text.Length + 1) * sizeof(char);
        var hMem = GlobalAlloc(GMEM_MOVEABLE, new UIntPtr((uint)byteCount));
        if (hMem == IntPtr.Zero)
            return Task.FromResult(false);

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return Task.FromResult(false);
        }

        try
        {
            var chars = text.ToCharArray();
            Marshal.Copy(chars, 0, ptr, chars.Length);
            Marshal.WriteInt16(ptr, chars.Length * sizeof(char), 0);
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        var ok = false;
        if (OpenClipboard(IntPtr.Zero))
        {
            try
            {
                EmptyClipboard();
                ok = SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero;
            }
            finally
            {
                CloseClipboard();
            }
        }

        if (!ok)
            GlobalFree(hMem);

        return Task.FromResult(ok);
    }

    private byte[]? CapturePng(int width, int height)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            return null;

        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ComputerUse] Failed to capture screen");
            return null;
        }
    }

    private void SendMouseEvent(uint flags, uint mouseData = 0)
    {
        var inputs = new[]
        {
            CreateMouseInput(flags, mouseData)
        };
        SendInputs(inputs);
    }

    private static INPUT CreateMouseInput(uint flags, uint mouseData)
    {
        return new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static INPUT CreateKeyInput(ushort vk, ushort scan, uint time, uint flags)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = flags,
                    time = time,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static void AddKeyInputs(List<INPUT> inputs, ushort vk, ushort scan, uint flags)
    {
        inputs.Add(CreateKeyInput(vk, scan, 0, 0));
        inputs.Add(CreateKeyInput(vk, scan, 0, flags));
    }

    private static void SendInputs(IReadOnlyList<INPUT> inputs)
    {
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var builder = new StringBuilder(512);
        GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static ushort MapKeyCode(string key)
    {
        return key switch
        {
            "enter" or "return" => VK_RETURN,
            "tab" => VK_TAB,
            "space" => VK_SPACE,
            "backspace" => VK_BACK,
            "delete" or "del" => VK_DELETE,
            "escape" or "esc" => VK_ESCAPE,
            "home" => VK_HOME,
            "end" => VK_END,
            "pageup" => VK_PRIOR,
            "pagedown" => VK_NEXT,
            "up" or "arrowup" => VK_UP,
            "down" or "arrowdown" => VK_DOWN,
            "left" or "arrowleft" => VK_LEFT,
            "right" or "arrowright" => VK_RIGHT,
            "insert" or "ins" => VK_INSERT,
            "f1" => VK_F1,
            "f2" => VK_F2,
            "f3" => VK_F3,
            "f4" => VK_F4,
            "f5" => VK_F5,
            "f6" => VK_F6,
            "f7" => VK_F7,
            "f8" => VK_F8,
            "f9" => VK_F9,
            "f10" => VK_F10,
            "f11" => VK_F11,
            "f12" => VK_F12,
            _ when key.Length == 1 => char.ToUpperInvariant(key[0]),
            _ => 0
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const ushort VK_UNDEFINED = 0;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_PAUSE = 0x13;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_END = 0x23;
    private const ushort VK_HOME = 0x24;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_UP = 0x26;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_INSERT = 0x2D;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_PRIOR = 0x21;
    private const ushort VK_NEXT = 0x22;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_F1 = 0x70;
    private const ushort VK_F2 = 0x71;
    private const ushort VK_F3 = 0x72;
    private const ushort VK_F4 = 0x73;
    private const ushort VK_F5 = 0x74;
    private const ushort VK_F6 = 0x75;
    private const ushort VK_F7 = 0x76;
    private const ushort VK_F8 = 0x77;
    private const ushort VK_F9 = 0x78;
    private const ushort VK_F10 = 0x79;
    private const ushort VK_F11 = 0x7A;
    private const ushort VK_F12 = 0x7B;

    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;
    private const int SW_SHOW = 5;

    private const uint WM_CLOSE = 0x0010;

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
}
