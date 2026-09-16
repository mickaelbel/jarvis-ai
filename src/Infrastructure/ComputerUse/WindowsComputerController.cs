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

        // DPI-aware capture: GetSystemMetrics returns logical pixels; scale to physical
        var logicalWidth = GetSystemMetrics(SM_CXSCREEN);
        var logicalHeight = GetSystemMetrics(SM_CYSCREEN);
        if (logicalWidth <= 0 || logicalHeight <= 0)
            return null;

        var scaleX = 1.0;
        var scaleY = 1.0;
        try
        {
            var hdc = GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                const int LOGPIXELSX = 88;
                const int LOGPIXELSY = 90;
                var dpiX = GetDeviceCaps(hdc, LOGPIXELSX);
                var dpiY = GetDeviceCaps(hdc, LOGPIXELSY);
                scaleX = dpiX / 96.0;
                scaleY = dpiY / 96.0;
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        catch { /* fallback to logical pixels */ }

        var width = (int)(logicalWidth * scaleX);
        var height = (int)(logicalHeight * scaleY);

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

    /// <summary>
    /// Smooth mouse movement: interpolates from current position to target
    /// with small steps and delays, like a human.
    /// </summary>
    public async Task<bool> MoveMouseSmoothAsync(int targetX, int targetY, int? steps = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return false;

        GetCursorPos(out var start);
        var dx = targetX - start.X;
        var dy = targetY - start.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        // Adaptive step count: ~3px per step, min 5, max 50
        var stepCount = steps ?? Math.Clamp((int)(distance / 3), 5, 50);
        var delayPerStep = Math.Max(1, 15 / stepCount); // ~15ms total movement time

        for (int i = 1; i <= stepCount; i++)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            var t = (double)i / stepCount;
            // Ease-in-out curve for natural feel
            var ease = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

            var x = (int)(start.X + dx * ease);
            var y = (int)(start.Y + dy * ease);

            // Add slight random jitter for human-like movement
            if (i < stepCount)
            {
                x += System.Random.Shared.Next(-1, 2);
                y += System.Random.Shared.Next(-1, 2);
            }

            SetCursorPos(x, y);
            await Task.Delay(delayPerStep + System.Random.Shared.Next(0, 3), cancellationToken);
        }

        // Ensure exact final position
        SetCursorPos(targetX, targetY);
        _logger.LogDebug("[ComputerUse] Smooth mouse move ({X1},{Y1}) → ({X2},{Y2}), {Steps} steps",
            start.X, start.Y, targetX, targetY, stepCount);
        return true;
    }

    /// <summary>
    /// Click on a background window without focusing it.
    /// Uses PostMessage to send mouse events directly to the window.
    /// </summary>
    public async Task<bool> ClickBackgroundAsync(IntPtr hWnd, int x, int y, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || hWnd == IntPtr.Zero)
            return false;

        var (downMsg, upMsg) = button switch
        {
            MouseButton.Right => (WM_RBUTTONDOWN, WM_RBUTTONUP),
            MouseButton.Middle => (WM_MBUTTONDOWN, WM_MBUTTONUP),
            _ => (WM_LBUTTONDOWN, WM_LBUTTONUP)
        };

        var lParam = MakeLParam(x, y);
        PostMessage(hWnd, downMsg, IntPtr.Zero, lParam);
        await Task.Delay(30 + Random.Shared.Next(0, 20)); // Human-like delay
        PostMessage(hWnd, upMsg, IntPtr.Zero, lParam);

        _logger.LogDebug("[ComputerUse] Background click {Button} at ({X},{Y}) on handle {Handle}", button, x, y, hWnd);
        return true;
    }

    /// <summary>
    /// Type text to a background window using PostMessage (WM_CHAR).
    /// </summary>
    public async Task<bool> TypeBackgroundAsync(IntPtr hWnd, string text, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || hWnd == IntPtr.Zero || string.IsNullOrEmpty(text))
            return false;

        foreach (var c in text)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            PostMessage(hWnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            await Task.Delay(10 + Random.Shared.Next(0, 15)); // Human-like typing speed
        }

        _logger.LogDebug("[ComputerUse] Background typed {Len} chars to handle {Handle}", text.Length, hWnd);
        return true;
    }

    /// <summary>
    /// Send a key to a background window.
    /// </summary>
    public async Task<bool> KeyBackgroundAsync(IntPtr hWnd, ushort vk, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || hWnd == IntPtr.Zero)
            return false;

        var lParamDown = MakeKeyLParam(vk, 0, false, false, false);
        var lParamUp = MakeKeyLParam(vk, 0, true, false, false);

        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)lParamDown);
        await Task.Delay(20);
        PostMessage(hWnd, WM_KEYUP, (IntPtr)vk, (IntPtr)lParamUp);

        return true;
    }

    private static int MakeLParam(int x, int y) => (y << 16) | (x & 0xFFFF);

    private static int MakeKeyLParam(ushort vk, ushort scan, bool extended, bool up, bool transition)
    {
        var scanCode = scan != 0 ? scan : (ushort)0x45; // default scan code
        int lParam = 1;
        lParam |= scanCode << 16;
        if (extended) lParam |= 1 << 24;
        if (up) lParam |= 1 << 30;
        if (transition) lParam |= 1 << 31;
        return lParam;
    }

    public async Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        // Smooth mouse movement to target
        if (x.HasValue && y.HasValue)
        {
            await MoveMouseSmoothAsync(x.Value, y.Value, cancellationToken: cancellationToken);
            await Task.Delay(50); // Small pause before click
        }

        var (down, up) = button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        SendMouseEvent(down);
        await Task.Delay(30 + Random.Shared.Next(0, 15)); // Human-like delay
        SendMouseEvent(up);

        _logger.LogDebug("[ComputerUse] Clicked {Button} at ({X},{Y})", button, x, y);
        return true;
    }

    public async Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (x.HasValue && y.HasValue)
            await MoveMouseSmoothAsync(x.Value, y.Value, cancellationToken: cancellationToken);

        var (down, up) = button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        for (var i = 0; i < 2; i++)
        {
            SendMouseEvent(down);
            await Task.Delay(30);
            SendMouseEvent(up);
            await Task.Delay(50);
        }

        _logger.LogDebug("[ComputerUse] Double-clicked {Button} at ({X},{Y})", button, x, y);
        return true;
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

        var combo = keyCombination.Trim();

        // Un caractère seul (ex: "+") ne doit pas être découpé sur le '+' : on le
        // traite comme une touche unique. Sinon on découpe la combinaison "ctrl+s".
        var parts = combo.Length == 1
            ? new[] { combo.ToLowerInvariant() }
            : combo.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return Task.FromResult(false);

        var keys = new List<ushort>(parts.Length);
        foreach (var part in parts)
        {
            var vk = MapKeyCode(part);
            if (vk == VK_UNDEFINED)
            {
                _logger.LogDebug("[ComputerUse] Touche inconnue: {Key} (combinaison '{Combo}')", part, keyCombination);
                return Task.FromResult(false);
            }
            keys.Add(vk);
        }

        // Toutes les touches sauf la DERNIÈRE sont maintenues pendant que la
        // dernière est tapée puis relâchée. Ce modèle permet aussi de presser une
        // touche SEULE (ex: "win" pour ouvrir le menu Démarrer, "alt", "ctrl"…) :
        // avant, "win" n'était vu que comme modificateur, mainKey restait 0 et la
        // touche Windows n'était JAMAIS enfoncée.
        var inputs = new List<INPUT>();
        for (var i = 0; i < keys.Count - 1; i++)
            inputs.Add(CreateKeyInput(keys[i], 0, 0, 0));
        inputs.Add(CreateKeyInput(keys[^1], 0, 0, 0));
        inputs.Add(CreateKeyInput(keys[^1], 0, 0, KEYEVENTF_KEYUP));
        for (var i = keys.Count - 2; i >= 0; i--)
            inputs.Add(CreateKeyInput(keys[i], 0, 0, KEYEVENTF_KEYUP));

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

    public async Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var hWnd = new IntPtr(handle);
        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        // Le verrou de premier plan Windows peut refuser SetForegroundWindow
        // (surtout en mode chat : l'utilisateur vient de cliquer dans Jarvis).
        // On tente le vol de focus classique, puis on VÉRIFIE réellement, et en
        // dernier recours on clique sur la barre de titre : un vrai clic souris
        // octroie TOUJOURS le premier plan, verrou ou pas.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // Déjà au premier plan → parfait.
            if (GetForegroundWindow() == hWnd)
                return true;

            TryStealForeground(hWnd);
            await Task.Delay(150, cancellationToken);
            if (GetForegroundWindow() == hWnd)
                return true;

            // Fallback : clic souris réel sur la barre de titre de la fenêtre.
            if (GetWindowRect(hWnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
            {
                var clickX = rect.Left + Math.Max(10, (rect.Right - rect.Left) / 2);
                var clickY = rect.Top + Math.Max(10, (rect.Bottom - rect.Top) / 12);
                SetCursorPos(clickX, clickY);
                SendMouseEvent(MOUSEEVENTF_LEFTDOWN);
                await Task.Delay(30, cancellationToken);
                SendMouseEvent(MOUSEEVENTF_LEFTUP);
                await Task.Delay(150, cancellationToken);
                if (GetForegroundWindow() == hWnd)
                    return true;
            }
        }

        var focused = GetForegroundWindow() == hWnd;
        _logger.LogDebug("[ComputerUse] Focusing window handle {Handle}: {Result}", handle, focused);
        return focused;
    }

    private void TryStealForeground(IntPtr hWnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hWnd)
            return;

        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(hWnd, out _);
        var currentThread = GetCurrentThreadId();

        // Windows refuse SetForegroundWindow pour les processus en arrière-plan.
        // On attache notre file d'entrée aux deux threads et on simule une
        // pression ALT inoffensive pour libérer le verrou de premier plan.
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

    public Task<ScreenCapture?> CaptureWindowAsync(long handle, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || handle == 0)
            return Task.FromResult<ScreenCapture?>(null);

        var hWnd = new IntPtr(handle);

        // DWMWA_EXTENDED_FRAME_BOUNDS donne les dimensions réelles de la fenêtre en tenant compte
        // des bordures invisibles Windows 10/11. Fallback sur GetWindowRect.
        RECT frameRect;
        try
        {
            frameRect = default;
            var hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out frameRect, Marshal.SizeOf<RECT>());
            if (hr != 0 || (frameRect.Right == 0 && frameRect.Bottom == 0))
                _ = GetWindowRect(hWnd, out frameRect);
        }
        catch
        {
            if (!GetWindowRect(hWnd, out frameRect))
                return Task.FromResult<ScreenCapture?>(null);
        }

        var width = frameRect.Right - frameRect.Left;
        var height = frameRect.Bottom - frameRect.Top;
        if (width <= 0 || height <= 0)
            return Task.FromResult<ScreenCapture?>(null);

        var png = CaptureWindowPng(hWnd, width, height);
        if (png is null)
            return Task.FromResult<ScreenCapture?>(null);

        _logger.LogDebug("[ComputerUse] Captured window {Handle} {W}x{H}", handle, width, height);
        return Task.FromResult<ScreenCapture?>(new ScreenCapture(png, width, height, 0, 0));
    }

    private static byte[]? CaptureWindowPng(IntPtr hWnd, int width, int height)
    {
        try
        {
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdcBitmap = g.GetHdc();
                try
                {
                    // PW_RENDERFULLCONTENT (0x2) : capture le rendu DWM complet (applications modernes).
                    if (!PrintWindow(hWnd, hdcBitmap, PW_RENDERFULLCONTENT))
                    {
                        // Fallback sans flag pour certaines fenêtres classiques.
                        if (!PrintWindow(hWnd, hdcBitmap, 0))
                            return null;
                    }
                }
                finally
                {
                    g.ReleaseHdc(hdcBitmap);
                }
            }
            using var mem = new MemoryStream();
            bmp.Save(mem, ImageFormat.Png);
            return mem.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<string?>(null);

        string? result = null;
        // Retry court sur contention : Excel/Office tiennent le presse-papiers parfois.
        for (var attempt = 0; attempt < 5; attempt++)
        {
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
                break;
            }
            if (attempt < 4) Thread.Sleep(60);
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
        // Retry court sur contention (Excel/Office peuvent tenir le presse-papiers).
        for (var attempt = 0; attempt < 5 && !ok; attempt++)
        {
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
            else if (attempt < 4)
            {
                Thread.Sleep(60);
            }
        }

        if (!ok)
            GlobalFree(hMem);

        return Task.FromResult(ok);
    }

    public async Task<bool> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return false;

        // Smooth move to start position
        await MoveMouseSmoothAsync(fromX, fromY, cancellationToken: cancellationToken);
        await Task.Delay(50);

        var (down, up) = button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        SendMouseEvent(down);
        await Task.Delay(30);

        // Smooth drag with easing
        var distance = Math.Sqrt(Math.Pow(toX - fromX, 2) + Math.Pow(toY - fromY, 2));
        var steps = Math.Clamp((int)(distance / 5), 10, 80);

        for (int i = 1; i <= steps; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var t = (double)i / steps;
            var ease = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            var x = (int)(fromX + (toX - fromX) * ease) + System.Random.Shared.Next(-1, 2);
            var y = (int)(fromY + (toY - fromY) * ease) + System.Random.Shared.Next(-1, 2);
            SetCursorPos(x, y);
            await Task.Delay(8 + System.Random.Shared.Next(0, 5), cancellationToken);
        }

        SetCursorPos(toX, toY);
        await Task.Delay(20);
        SendMouseEvent(up);

        _logger.LogDebug("[ComputerUse] Dragged ({FromX},{FromY}) → ({ToX},{ToY}), {Steps} steps", fromX, fromY, toX, toY, steps);
        return true;
    }

    public Task<bool> HoverAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        SetCursorPos(x, y);
        _logger.LogDebug("[ComputerUse] Hovered at ({X},{Y})", x, y);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<MonitorInfo>> ListMonitorsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IReadOnlyList<MonitorInfo>>(Array.Empty<MonitorInfo>());

        var monitors = new List<MonitorInfo>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            if (GetMonitorInfo(hMonitor, out var info))
            {
                var rc = info.rcMonitor;
                var width = rc.Right - rc.Left;
                var height = rc.Bottom - rc.Top;
                var isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                var index = monitors.Count;
                monitors.Add(new MonitorInfo(index, rc.Left, rc.Top, width, height, isPrimary));
            }
            return true;
        }, IntPtr.Zero);

        return Task.FromResult<IReadOnlyList<MonitorInfo>>(monitors);
    }

    public Task<ScreenCapture?> CaptureMonitorAsync(int monitorIndex, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<ScreenCapture?>(null);

        var monitors = new List<(int x, int y, int w, int h)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            if (GetMonitorInfo(hMonitor, out var info))
            {
                var rc = info.rcMonitor;
                monitors.Add((rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top));
            }
            return true;
        }, IntPtr.Zero);

        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            return Task.FromResult<ScreenCapture?>(null);

        var m = monitors[monitorIndex];
        try
        {
            using var bitmap = new Bitmap(m.w, m.h, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(m.x, m.y, 0, 0, new Size(m.w, m.h), CopyPixelOperation.SourceCopy);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            var png = stream.ToArray();

            GetCursorPos(out var point);
            _logger.LogInformation("[ComputerUse] Captured monitor {Index} {W}x{H}", monitorIndex, m.w, m.h);
            return Task.FromResult<ScreenCapture?>(new ScreenCapture(png, m.w, m.h, point.X, point.Y));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ComputerUse] Failed to capture monitor {Index}", monitorIndex);
            return Task.FromResult<ScreenCapture?>(null);
        }
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

    internal static ushort MapKeyCode(string key)
    {
        return key switch
        {
            // ── Modificateurs (utilisables SEULS ou en combinaison) ──────────
            "win" or "lwin" or "super" or "meta" or "windows" => VK_LWIN,
            "rwin" => VK_RWIN,
            "ctrl" or "control" or "lctrl" => VK_CONTROL,
            "rctrl" => VK_RCONTROL,
            "alt" or "lalt" => VK_MENU,
            "ralt" or "altgr" => VK_RMENU,
            "shift" or "lshift" => VK_SHIFT,
            "rshift" => VK_RSHIFT,

            // ── Édition / navigation ────────────────────────────────────────
            "enter" or "return" => VK_RETURN,
            "numpadenter" or "num-enter" or "numpad_enter" or "enternum" => VK_RETURN,
            "tab" => VK_TAB,
            "space" or "espace" => VK_SPACE,
            "backspace" => VK_BACK,
            "delete" or "del" => VK_DELETE,
            "escape" or "esc" => VK_ESCAPE,
            "home" => VK_HOME,
            "end" => VK_END,
            "pageup" or "pgup" => VK_PRIOR,
            "pagedown" or "pgdn" or "pgdown" => VK_NEXT,
            "up" or "arrowup" => VK_UP,
            "down" or "arrowdown" => VK_DOWN,
            "left" or "arrowleft" => VK_LEFT,
            "right" or "arrowright" => VK_RIGHT,
            "insert" or "ins" => VK_INSERT,

            // ── Verrouillage / système ──────────────────────────────────────
            "numlock" or "num" => VK_NUMLOCK,
            "capslock" or "caps" => VK_CAPITAL,
            "scrolllock" or "scroll" or "scrlk" => VK_SCROLL,
            "printscreen" or "prtsc" or "print" or "imprécran" => VK_SNAPSHOT,
            "pause" or "break" => VK_PAUSE,
            "apps" or "menu" or "contextmenu" => VK_APPS,

            // ── Pavé numérique ──────────────────────────────────────────────
            "numpad0" or "num0" => VK_NUMPAD0,
            "numpad1" or "num1" => VK_NUMPAD1,
            "numpad2" or "num2" => VK_NUMPAD2,
            "numpad3" or "num3" => VK_NUMPAD3,
            "numpad4" or "num4" => VK_NUMPAD4,
            "numpad5" or "num5" => VK_NUMPAD5,
            "numpad6" or "num6" => VK_NUMPAD6,
            "numpad7" or "num7" => VK_NUMPAD7,
            "numpad8" or "num8" => VK_NUMPAD8,
            "numpad9" or "num9" => VK_NUMPAD9,
            "numpadadd" or "numadd" or "add" => VK_ADD,
            "numpadsubtract" or "numsub" or "subtract" => VK_SUBTRACT,
            "numpadmultiply" or "nummul" or "multiply" => VK_MULTIPLY,
            "numpaddivide" or "numdiv" or "divide" => VK_DIVIDE,
            "numpaddecimal" or "numdec" or "decimal" => VK_DECIMAL,

            // ── Ponctuation (noms explicites) ───────────────────────────────
            "plus" => VK_OEM_PLUS,
            "minus" => VK_OEM_MINUS,
            "comma" => VK_OEM_COMMA,
            "period" or "dot" => VK_OEM_PERIOD,
            "slash" => VK_OEM_2,
            "backslash" => VK_OEM_5,
            "semicolon" => VK_OEM_1,
            "quote" or "apostrophe" => VK_OEM_7,
            "backtick" or "grave" => VK_OEM_3,
            "bracketleft" or "leftbracket" or "ouvertcrochet" => VK_OEM_4,
            "bracketright" or "rightbracket" or "fermecrochet" => VK_OEM_6,
            "equal" or "equals" or "egal" => VK_OEM_PLUS,

            // ── Touches de fonction F1→F24 ──────────────────────────────────
            "f1" => VK_F1, "f2" => VK_F2, "f3" => VK_F3, "f4" => VK_F4,
            "f5" => VK_F5, "f6" => VK_F6, "f7" => VK_F7, "f8" => VK_F8,
            "f9" => VK_F9, "f10" => VK_F10, "f11" => VK_F11, "f12" => VK_F12,
            "f13" => VK_F13, "f14" => VK_F14, "f15" => VK_F15, "f16" => VK_F16,
            "f17" => VK_F17, "f18" => VK_F18, "f19" => VK_F19, "f20" => VK_F20,
            "f21" => VK_F21, "f22" => VK_F22, "f23" => VK_F23, "f24" => VK_F24,

            _ => MapSingleCharKey(key)
        };
    }

    /// <summary>
    /// Repli pour une touche d'un seul caractère : lettres/digits → code ASCII/VK
    /// (identique), ponctuation → code OEM réel (sinon '+' ou '/' donnaient un code
    /// ASCII faux et la touche n'était pas enfoncée).
    /// </summary>
    private static ushort MapSingleCharKey(string key)
    {
        if (key.Length != 1) return VK_UNDEFINED;
        var c = key[0];

        if (c is >= 'a' and <= 'z') return (ushort)char.ToUpperInvariant(c);
        if (c is >= '0' and <= '9') return (ushort)c;

        return c switch
        {
            '+' or '=' => VK_OEM_PLUS,
            '-' or '_' => VK_OEM_MINUS,
            ',' or '<' => VK_OEM_COMMA,
            '.' or '>' => VK_OEM_PERIOD,
            '/' or '?' => VK_OEM_2,
            '\\' or '|' => VK_OEM_5,
            ';' or ':' => VK_OEM_1,
            '\'' or '"' => VK_OEM_7,
            '`' or '~' => VK_OEM_3,
            '[' or '{' => VK_OEM_4,
            ']' or '}' => VK_OEM_6,
            _ => VK_UNDEFINED
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

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, out MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    private const int MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

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
    private const ushort VK_F13 = 0x7C;
    private const ushort VK_F14 = 0x7D;
    private const ushort VK_F15 = 0x7E;
    private const ushort VK_F16 = 0x7F;
    private const ushort VK_F17 = 0x80;
    private const ushort VK_F18 = 0x81;
    private const ushort VK_F19 = 0x82;
    private const ushort VK_F20 = 0x83;
    private const ushort VK_F21 = 0x84;
    private const ushort VK_F22 = 0x85;
    private const ushort VK_F23 = 0x86;
    private const ushort VK_F24 = 0x87;

    private const ushort VK_CAPITAL = 0x14;
    private const ushort VK_NUMLOCK = 0x90;
    private const ushort VK_SCROLL = 0x91;
    private const ushort VK_SNAPSHOT = 0x2C;
    private const ushort VK_APPS = 0x5D;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;

    private const ushort VK_NUMPAD0 = 0x60;
    private const ushort VK_NUMPAD1 = 0x61;
    private const ushort VK_NUMPAD2 = 0x62;
    private const ushort VK_NUMPAD3 = 0x63;
    private const ushort VK_NUMPAD4 = 0x64;
    private const ushort VK_NUMPAD5 = 0x65;
    private const ushort VK_NUMPAD6 = 0x66;
    private const ushort VK_NUMPAD7 = 0x67;
    private const ushort VK_NUMPAD8 = 0x68;
    private const ushort VK_NUMPAD9 = 0x69;
    private const ushort VK_MULTIPLY = 0x6A;
    private const ushort VK_ADD = 0x6B;
    private const ushort VK_SUBTRACT = 0x6D;
    private const ushort VK_DECIMAL = 0x6E;
    private const ushort VK_DIVIDE = 0x6F;

    private const ushort VK_OEM_1 = 0xBA;
    private const ushort VK_OEM_PLUS = 0xBB;
    private const ushort VK_OEM_COMMA = 0xBC;
    private const ushort VK_OEM_MINUS = 0xBD;
    private const ushort VK_OEM_PERIOD = 0xBE;
    private const ushort VK_OEM_2 = 0xBF;
    private const ushort VK_OEM_3 = 0xC0;
    private const ushort VK_OEM_4 = 0xDB;
    private const ushort VK_OEM_5 = 0xDC;
    private const ushort VK_OEM_6 = 0xDD;
    private const ushort VK_OEM_7 = 0xDE;

    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;
    private const int SW_SHOW = 5;

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
}
