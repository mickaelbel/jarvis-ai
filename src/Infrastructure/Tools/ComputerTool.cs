using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ComputerTool : ITool
{
    private readonly IComputerController _controller;
    private readonly ILogger<ComputerTool> _logger;

    public string Name => "computer";
    public string Description => "Computer use: operate the screen, mouse, keyboard and windows. Actions: capture_screen, move_mouse, click, double_click, scroll, type_text, press_key, list_windows, focus_window, get_foreground_window, minimize_window, maximize_window, restore_window, close_window, move_window, resize_window, get_window_rect, get_clipboard, set_clipboard";
    public string Category => "computer_use";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Operation: capture_screen, move_mouse, click, double_click, scroll, type_text, press_key, list_windows, focus_window, get_foreground_window, minimize_window, maximize_window, restore_window, close_window, move_window, resize_window, get_window_rect, get_clipboard, set_clipboard", typeof(string), required: true),
        new ToolParameter("x", "X coordinate (screen pixels)", typeof(int)),
        new ToolParameter("y", "Y coordinate (screen pixels)", typeof(int)),
        new ToolParameter("width", "New width for resize_window (screen pixels)", typeof(int)),
        new ToolParameter("height", "New height for resize_window (screen pixels)", typeof(int)),
        new ToolParameter("button", "Mouse button: left, right, middle", typeof(string)),
        new ToolParameter("text", "Text to type (for type_text)", typeof(string)),
        new ToolParameter("keys", "Key combination like ctrl+c or enter (for press_key)", typeof(string)),
        new ToolParameter("deltaY", "Scroll amount in lines, positive = up (for scroll)", typeof(int)),
        new ToolParameter("handle", "Window handle from list_windows (for focus_window and window actions)", typeof(long)),
        new ToolParameter("clipboard_text", "Text to put in the clipboard (for set_clipboard)", typeof(string))
    };

    public ComputerTool(IComputerController controller, ILogger<ComputerTool> logger)
    {
        _controller = controller;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("x", out var xStr);
        parameters.TryGetValue("y", out var yStr);
        parameters.TryGetValue("width", out var widthStr);
        parameters.TryGetValue("height", out var heightStr);
        parameters.TryGetValue("button", out var buttonStr);
        parameters.TryGetValue("text", out var text);
        parameters.TryGetValue("keys", out var keys);
        parameters.TryGetValue("deltaY", out var deltaYStr);
        parameters.TryGetValue("handle", out var handleStr);
        parameters.TryGetValue("clipboard_text", out var clipboardText);

        var actionLower = action?.ToLowerInvariant();

        try
        {
            return actionLower switch
            {
                "capture_screen" => await CaptureScreenAsync(cancellationToken),
                "move_mouse" => await MoveMouseAsync(xStr, yStr),
                "click" => await ClickAsync(xStr, yStr, buttonStr),
                "double_click" => await DoubleClickAsync(xStr, yStr, buttonStr),
                "scroll" => await ScrollAsync(deltaYStr),
                "type_text" => await TypeTextAsync(text),
                "press_key" => await PressKeyAsync(keys),
                "list_windows" => await ListWindowsAsync(),
                "focus_window" => await FocusWindowAsync(handleStr),
                "get_foreground_window" => await GetForegroundWindowAsync(),
                "minimize_window" => await MinimizeWindowAsync(handleStr),
                "maximize_window" => await MaximizeWindowAsync(handleStr),
                "restore_window" => await RestoreWindowAsync(handleStr),
                "close_window" => await CloseWindowAsync(handleStr),
                "move_window" => await MoveWindowAsync(handleStr, xStr, yStr),
                "resize_window" => await ResizeWindowAsync(handleStr, widthStr, heightStr),
                "get_window_rect" => await GetWindowRectAsync(handleStr),
                "get_clipboard" => await GetClipboardAsync(),
                "set_clipboard" => await SetClipboardAsync(clipboardText),
                _ => ToolResult.Failed($"Unknown action: {action}. Valid: capture_screen, move_mouse, click, double_click, scroll, type_text, press_key, list_windows, focus_window, get_foreground_window, minimize_window, maximize_window, restore_window, close_window, move_window, resize_window, get_window_rect, get_clipboard, set_clipboard")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ComputerTool] Action {Action} failed", action);
            return ToolResult.Failed($"Computer use error: {ex.Message}");
        }
    }

    private async Task<ToolResult> CaptureScreenAsync(CancellationToken cancellationToken)
    {
        if (!_controller.IsAvailable)
            return ToolResult.Failed("Computer control is only available on Windows");

        var capture = await _controller.CaptureScreenAsync(cancellationToken);
        if (capture is null)
            return ToolResult.Failed("Failed to capture the screen");

        var directory = Path.Combine(Path.GetTempPath(), "jarvis");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"capture_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
        await File.WriteAllBytesAsync(path, capture.PngBytes, cancellationToken);

        var info = new
        {
            path,
            capture.Width,
            capture.Height,
            cursorX = capture.CursorX,
            cursorY = capture.CursorY,
            imageBase64 = Convert.ToBase64String(capture.PngBytes)
        };

        _logger.LogInformation("[ComputerTool] Screen captured to {Path}", path);
        return ToolResult.Succeeded(JsonSerializer.Serialize(info));
    }

    private async Task<ToolResult> MoveMouseAsync(string? xStr, string? yStr)
    {
        if (string.IsNullOrWhiteSpace(xStr) || string.IsNullOrWhiteSpace(yStr) ||
            !TryParseCoordinates(xStr, yStr, out var x, out var y))
            return ToolResult.Failed("Parameters 'x' and 'y' are required integers");

        var moved = await _controller.MoveMouseAsync(x.GetValueOrDefault(), y.GetValueOrDefault());
        return moved
            ? ToolResult.Succeeded($"Mouse moved to ({x}, {y})")
            : ToolResult.Failed("Failed to move mouse");
    }

    private async Task<ToolResult> ClickAsync(string? xStr, string? yStr, string? buttonStr)
    {
        TryParseCoordinates(xStr, yStr, out var x, out var y);
        var button = ParseButton(buttonStr);

        var clicked = await _controller.ClickAsync(button, x, y);
        return clicked
            ? ToolResult.Succeeded($"Clicked {button} at ({x ?? 0}, {y ?? 0})")
            : ToolResult.Failed("Failed to click");
    }

    private async Task<ToolResult> DoubleClickAsync(string? xStr, string? yStr, string? buttonStr)
    {
        TryParseCoordinates(xStr, yStr, out var x, out var y);
        var button = ParseButton(buttonStr);

        var clicked = await _controller.DoubleClickAsync(button, x, y);
        return clicked
            ? ToolResult.Succeeded($"Double-clicked {button} at ({x ?? 0}, {y ?? 0})")
            : ToolResult.Failed("Failed to double-click");
    }

    private async Task<ToolResult> ScrollAsync(string? deltaYStr)
    {
        if (!int.TryParse(deltaYStr, out var deltaY))
            return ToolResult.Failed("Parameter 'deltaY' is required as an integer");

        var scrolled = await _controller.ScrollAsync(deltaY);
        return scrolled
            ? ToolResult.Succeeded($"Scrolled by {deltaY}")
            : ToolResult.Failed("Failed to scroll");
    }

    private async Task<ToolResult> TypeTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return ToolResult.Failed("Parameter 'text' is required");

        var typed = await _controller.TypeTextAsync(text);
        return typed
            ? ToolResult.Succeeded("Text typed")
            : ToolResult.Failed("Failed to type text");
    }

    private async Task<ToolResult> PressKeyAsync(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
            return ToolResult.Failed("Parameter 'keys' is required (e.g. ctrl+c)");

        var pressed = await _controller.PressKeyAsync(keys);
        return pressed
            ? ToolResult.Succeeded($"Key pressed: {keys}")
            : ToolResult.Failed($"Failed to press key combination: {keys}");
    }

    private async Task<ToolResult> ListWindowsAsync()
    {
        var windows = await _controller.ListWindowsAsync();
        if (windows.Count == 0)
            return ToolResult.Succeeded("No visible windows found");

        var payload = windows.Select(w => new { w.Handle, w.Title, w.IsFocused, w.X, w.Y, w.Width, w.Height });
        return ToolResult.Succeeded(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<ToolResult> FocusWindowAsync(string? handleStr)
    {
        if (!long.TryParse(handleStr, out var handle))
            return ToolResult.Failed("Parameter 'handle' is required (from list_windows)");

        var focused = await _controller.FocusWindowAsync(handle);
        return focused
            ? ToolResult.Succeeded($"Window focused: {handle}")
            : ToolResult.Failed($"Failed to focus window: {handle}");
    }

    private async Task<ToolResult> GetForegroundWindowAsync()
    {
        var handle = await _controller.GetForegroundWindowAsync();
        return handle == 0
            ? ToolResult.Failed("No foreground window found")
            : ToolResult.Succeeded($"Foreground window handle: {handle}");
    }

    private async Task<ToolResult> MinimizeWindowAsync(string? handleStr)
        => await WindowStateAsync(handleStr, "minimized", h => _controller.MinimizeWindowAsync(h));

    private async Task<ToolResult> MaximizeWindowAsync(string? handleStr)
        => await WindowStateAsync(handleStr, "maximized", h => _controller.MaximizeWindowAsync(h));

    private async Task<ToolResult> RestoreWindowAsync(string? handleStr)
        => await WindowStateAsync(handleStr, "restored", h => _controller.RestoreWindowAsync(h));

    private async Task<ToolResult> WindowStateAsync(string? handleStr, string verb, Func<long, Task<bool>> operation)
    {
        if (!long.TryParse(handleStr, out var handle))
            return ToolResult.Failed("Parameter 'handle' is required (from list_windows)");

        var ok = await operation(handle);
        return ok
            ? ToolResult.Succeeded($"Window {verb}: {handle}")
            : ToolResult.Failed($"Failed to {verb} window: {handle}");
    }

    private async Task<ToolResult> CloseWindowAsync(string? handleStr)
    {
        if (!long.TryParse(handleStr, out var handle))
            return ToolResult.Failed("Parameter 'handle' is required (from list_windows)");

        var closed = await _controller.CloseWindowAsync(handle);
        return closed
            ? ToolResult.Succeeded($"Close requested for window: {handle}")
            : ToolResult.Failed($"Failed to close window: {handle}");
    }

    private async Task<ToolResult> MoveWindowAsync(string? handleStr, string? xStr, string? yStr)
    {
        if (!long.TryParse(handleStr, out var handle))
            return ToolResult.Failed("Parameter 'handle' is required (from list_windows)");
        if (!int.TryParse(xStr, out var x) || !int.TryParse(yStr, out var y))
            return ToolResult.Failed("Parameters 'x' and 'y' are required integers");

        var moved = await _controller.MoveWindowAsync(handle, x, y);
        return moved
            ? ToolResult.Succeeded($"Window moved to ({x}, {y}): {handle}")
            : ToolResult.Failed($"Failed to move window: {handle}");
    }

    private async Task<ToolResult> ResizeWindowAsync(string? handleStr, string? widthStr, string? heightStr)
    {
        if (!long.TryParse(handleStr, out var handle))
            return ToolResult.Failed("Parameter 'handle' is required (from list_windows)");
        if (!int.TryParse(widthStr, out var width) || !int.TryParse(heightStr, out var height))
            return ToolResult.Failed("Parameters 'width' and 'height' are required integers");

        var resized = await _controller.ResizeWindowAsync(handle, width, height);
        return resized
            ? ToolResult.Succeeded($"Window resized to {width}x{height}: {handle}")
            : ToolResult.Failed($"Failed to resize window: {handle}");
    }

    private async Task<ToolResult> GetWindowRectAsync(string? handleStr)
    {
        if (!long.TryParse(handleStr, out var handle))
            return ToolResult.Failed("Parameter 'handle' is required (from list_windows)");

        var rect = await _controller.GetWindowRectAsync(handle);
        return rect is null
            ? ToolResult.Failed($"Failed to get bounds for window: {handle}")
            : ToolResult.Succeeded($"Window bounds: ({rect.X}, {rect.Y}) {rect.Width}x{rect.Height}");
    }

    private async Task<ToolResult> GetClipboardAsync()
    {
        var text = await _controller.GetClipboardAsync();
        return text is null
            ? ToolResult.Failed("Clipboard is empty or unavailable")
            : ToolResult.Succeeded($"Clipboard: {text}");
    }

    private async Task<ToolResult> SetClipboardAsync(string? text)
    {
        if (text is null)
            return ToolResult.Failed("Parameter 'clipboard_text' is required");

        var updated = await _controller.SetClipboardAsync(text);
        return updated
            ? ToolResult.Succeeded("Clipboard updated")
            : ToolResult.Failed("Failed to update clipboard");
    }

    private static bool TryParseCoordinates(string? xStr, string? yStr, out int? x, out int? y)
    {
        x = null;
        y = null;

        if (string.IsNullOrEmpty(xStr) && string.IsNullOrEmpty(yStr))
            return true;

        if (int.TryParse(xStr, out var parsedX) && int.TryParse(yStr, out var parsedY))
        {
            x = parsedX;
            y = parsedY;
            return true;
        }

        return false;
    }

    private static MouseButton ParseButton(string? button)
    {
        return button?.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left
        };
    }
}
