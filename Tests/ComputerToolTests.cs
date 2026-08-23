using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace JarvisAI.Tests;

public sealed class ComputerToolTests
{
    private readonly FakeComputerController _controller = new();
    private readonly ComputerTool _tool;
    private readonly AgentContext _context = new("test command");

    public ComputerToolTests()
    {
        _tool = new ComputerTool(_controller, NullLogger<ComputerTool>.Instance);
    }

    [Fact]
    public async Task ComputerTool_captures_screen_and_returns_metadata()
    {
        _controller.Screen = new ScreenCapture(new byte[] { 1, 2, 3 }, 1920, 1080, 10, 20);

        var result = await _tool.ExecuteAsync(_context, Params("action", "capture_screen"));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.Equal(1920, root.GetProperty("Width").GetInt32());
        Assert.Equal(1080, root.GetProperty("Height").GetInt32());
        Assert.Equal(10, root.GetProperty("cursorX").GetInt32());
        Assert.Equal(20, root.GetProperty("cursorY").GetInt32());
        Assert.Equal("AQID", root.GetProperty("imageBase64").GetString());
        Assert.True(File.Exists(root.GetProperty("path").GetString()));
        Assert.Equal(1, _controller.CaptureCalls);
    }

    [Fact]
    public async Task ComputerTool_click_passes_coordinates_and_button()
    {
        var result = await _tool.ExecuteAsync(_context, Params(
            "action", "click",
            "x", "100",
            "y", "200",
            "button", "right"));

        Assert.True(result.Success);
        Assert.Equal(MouseButton.Right, _controller.LastButton);
        Assert.Equal(100, _controller.LastX);
        Assert.Equal(200, _controller.LastY);
    }

    [Fact]
    public async Task ComputerTool_type_text_passes_text()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "type_text", "text", "bonjour"));

        Assert.True(result.Success);
        Assert.Equal("bonjour", _controller.LastText);
    }

    [Fact]
    public async Task ComputerTool_press_key_passes_combination()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "press_key", "keys", "ctrl+c"));

        Assert.True(result.Success);
        Assert.Equal("ctrl+c", _controller.LastKeys);
    }

    [Fact]
    public async Task ComputerTool_scroll_passes_delta()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "scroll", "deltaY", "-120"));

        Assert.True(result.Success);
        Assert.Equal(-120, _controller.LastDeltaY);
    }

    [Fact]
    public async Task ComputerTool_list_windows_returns_json()
    {
        _controller.Windows = new[]
        {
            new WindowInfo(123, "Notepad", true, true),
            new WindowInfo(456, "Chrome", true, false)
        };

        var result = await _tool.ExecuteAsync(_context, Params("action", "list_windows"));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("Notepad", doc.RootElement[0].GetProperty("Title").GetString());
    }

    [Fact]
    public async Task ComputerTool_focus_window_passes_handle()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "focus_window", "handle", "123"));

        Assert.True(result.Success);
        Assert.Equal(123, _controller.LastHandle);
    }

    [Fact]
    public async Task ComputerTool_clipboard_roundtrip()
    {
        _controller.ClipboardText = "contenu presse-papiers";

        var get = await _tool.ExecuteAsync(_context, Params("action", "get_clipboard"));
        Assert.True(get.Success);
        Assert.Contains("contenu presse-papiers", get.Output);

        var set = await _tool.ExecuteAsync(_context, Params("action", "set_clipboard", "clipboard_text", "nouveau"));
        Assert.True(set.Success);
        Assert.Equal("nouveau", _controller.ClipboardText);
    }

    [Fact]
    public async Task ComputerTool_unknown_action_fails()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "teleport"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ComputerTool_capture_fails_when_controller_unavailable()
    {
        _controller.Available = false;

        var result = await _tool.ExecuteAsync(_context, Params("action", "capture_screen"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ComputerTool_move_mouse_requires_coordinates()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "move_mouse"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ComputerTool_get_foreground_window_returns_handle()
    {
        _controller.ForegroundHandle = 777;

        var result = await _tool.ExecuteAsync(_context, Params("action", "get_foreground_window"));

        Assert.True(result.Success);
        Assert.Contains("777", result.Output);
    }

    [Fact]
    public async Task ComputerTool_minimize_window_passes_handle()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "minimize_window", "handle", "123"));

        Assert.True(result.Success);
        Assert.Equal(123, _controller.LastWindowHandle);
        Assert.Equal("minimize", _controller.LastWindowOperation);
    }

    [Fact]
    public async Task ComputerTool_maximize_window_passes_handle()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "maximize_window", "handle", "456"));

        Assert.True(result.Success);
        Assert.Equal(456, _controller.LastWindowHandle);
        Assert.Equal("maximize", _controller.LastWindowOperation);
    }

    [Fact]
    public async Task ComputerTool_restore_window_passes_handle()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "restore_window", "handle", "789"));

        Assert.True(result.Success);
        Assert.Equal(789, _controller.LastWindowHandle);
        Assert.Equal("restore", _controller.LastWindowOperation);
    }

    [Fact]
    public async Task ComputerTool_close_window_passes_handle()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "close_window", "handle", "111"));

        Assert.True(result.Success);
        Assert.Equal(111, _controller.LastWindowHandle);
        Assert.Equal("close", _controller.LastWindowOperation);
    }

    [Fact]
    public async Task ComputerTool_move_window_passes_coordinates()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "move_window", "handle", "123", "x", "50", "y", "60"));

        Assert.True(result.Success);
        Assert.Equal(123, _controller.LastWindowHandle);
        Assert.Equal(50, _controller.LastWindowX);
        Assert.Equal(60, _controller.LastWindowY);
    }

    [Fact]
    public async Task ComputerTool_resize_window_passes_dimensions()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "resize_window", "handle", "123", "width", "800", "height", "600"));

        Assert.True(result.Success);
        Assert.Equal(123, _controller.LastWindowHandle);
        Assert.Equal(800, _controller.LastWindowWidth);
        Assert.Equal(600, _controller.LastWindowHeight);
    }

    [Fact]
    public async Task ComputerTool_get_window_rect_returns_bounds()
    {
        _controller.WindowRectResult = new WindowRect(10, 20, 800, 600);

        var result = await _tool.ExecuteAsync(_context, Params("action", "get_window_rect", "handle", "123"));

        Assert.True(result.Success);
        Assert.Contains("800x600", result.Output);
    }

    [Fact]
    public async Task ComputerTool_window_action_requires_handle()
    {
        var result = await _tool.ExecuteAsync(_context, Params("action", "minimize_window"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ComputerTool_list_windows_returns_bounds_in_payload()
    {
        _controller.Windows = new[]
        {
            new WindowInfo(123, "Notepad", true, true, 0, 0, 800, 600)
        };

        var result = await _tool.ExecuteAsync(_context, Params("action", "list_windows"));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal(800, doc.RootElement[0].GetProperty("Width").GetInt32());
        Assert.Equal(600, doc.RootElement[0].GetProperty("Height").GetInt32());
    }

    private static IReadOnlyDictionary<string, string> Params(params string[] keyValues)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < keyValues.Length; i += 2)
            dict[keyValues[i]] = keyValues[i + 1];
        return dict;
    }

    private sealed class FakeComputerController : IComputerController
    {
        public bool Available { get; set; } = true;
        public ScreenCapture? Screen { get; set; }
        public int CaptureCalls { get; private set; }
        public MouseButton LastButton { get; private set; }
        public int? LastX { get; private set; }
        public int? LastY { get; private set; }
        public string? LastText { get; private set; }
        public string? LastKeys { get; private set; }
        public int LastDeltaY { get; private set; }
        public long LastHandle { get; private set; }
        public string? ClipboardText { get; set; }
        public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();
        public long ForegroundHandle { get; set; }
        public string? LastWindowOperation { get; private set; }
        public long LastWindowHandle { get; private set; }
        public int LastWindowX { get; private set; }
        public int LastWindowY { get; private set; }
        public int LastWindowWidth { get; private set; }
        public int LastWindowHeight { get; private set; }
        public WindowRect? WindowRectResult { get; set; }

        public bool IsAvailable => Available;

        public Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            return Task.FromResult(Available ? Screen : null);
        }

        public Task<bool> MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            LastX = x;
            LastY = y;
            return Task.FromResult(Available);
        }

        public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
        {
            LastButton = button;
            LastX = x;
            LastY = y;
            return Task.FromResult(Available);
        }

        public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
        {
            LastButton = button;
            LastX = x;
            LastY = y;
            return Task.FromResult(Available);
        }

        public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default)
        {
            LastDeltaY = deltaY;
            return Task.FromResult(Available);
        }

        public Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default)
        {
            LastText = text;
            return Task.FromResult(Available);
        }

        public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default)
        {
            LastKeys = keyCombination;
            return Task.FromResult(Available);
        }

        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Windows);
        }

        public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default)
        {
            LastHandle = handle;
            return Task.FromResult(Available);
        }

        public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ClipboardText);
        }

        public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default)
        {
            ClipboardText = text;
            return Task.FromResult(Available);
        }

        public Task<long> GetForegroundWindowAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ForegroundHandle);

        public Task<bool> MinimizeWindowAsync(long handle, CancellationToken cancellationToken = default)
            => RecordWindowOperation("minimize", handle);

        public Task<bool> MaximizeWindowAsync(long handle, CancellationToken cancellationToken = default)
            => RecordWindowOperation("maximize", handle);

        public Task<bool> RestoreWindowAsync(long handle, CancellationToken cancellationToken = default)
            => RecordWindowOperation("restore", handle);

        public Task<bool> CloseWindowAsync(long handle, CancellationToken cancellationToken = default)
            => RecordWindowOperation("close", handle);

        public Task<bool> MoveWindowAsync(long handle, int x, int y, CancellationToken cancellationToken = default)
        {
            LastWindowOperation = "move";
            LastWindowHandle = handle;
            LastWindowX = x;
            LastWindowY = y;
            return Task.FromResult(Available);
        }

        public Task<bool> ResizeWindowAsync(long handle, int width, int height, CancellationToken cancellationToken = default)
        {
            LastWindowOperation = "resize";
            LastWindowHandle = handle;
            LastWindowWidth = width;
            LastWindowHeight = height;
            return Task.FromResult(Available);
        }

        public Task<WindowRect?> GetWindowRectAsync(long handle, CancellationToken cancellationToken = default)
        {
            LastWindowHandle = handle;
            return Task.FromResult(WindowRectResult);
        }

        private Task<bool> RecordWindowOperation(string operation, long handle)
        {
            LastWindowOperation = operation;
            LastWindowHandle = handle;
            return Task.FromResult(Available);
        }
    }
}
