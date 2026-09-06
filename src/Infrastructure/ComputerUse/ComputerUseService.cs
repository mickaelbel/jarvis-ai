using System.Diagnostics;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.ComputerUse;

/// <summary>
/// Orchestrates the computer use pipeline: capture the screen, run OCR, detect UI
/// elements, then act on them by label (observe -> decide -> click/type -> verify).
/// Caches the last observe results so click_element can reuse them without re-detecting.
/// </summary>
public sealed class ComputerUseService : IComputerUseService
{
    private readonly IComputerController _controller;
    private readonly IOcrService _ocr;
    private readonly IUiElementDetector _detector;
    private readonly ILogger<ComputerUseService> _logger;
    private readonly SemaphoreSlim _observeLock = new(1, 1);

    private sealed record CacheEntry(IReadOnlyList<UiElement> Elements, UiObservation Observation, DateTime Timestamp);
    private volatile CacheEntry? _cache;

    public ComputerUseService(
        IComputerController controller,
        IOcrService ocr,
        IUiElementDetector detector,
        ILogger<ComputerUseService> logger)
    {
        _controller = controller;
        _ocr = ocr;
        _detector = detector;
        _logger = logger;
    }

    public bool IsAvailable => _controller.IsAvailable;

    public async Task<UiObservation?> ObserveAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return null;

        var capture = await _controller.CaptureScreenAsync(cancellationToken);
        if (capture is null)
            return null;

        var ocrText = string.Empty;
        IReadOnlyList<UiElement> elements = Array.Empty<UiElement>();

        if (_ocr.OcrAvailable)
        {
            var ocr = await _ocr.ExtractTextAsync(capture.PngBytes, cancellationToken: cancellationToken);
            ocrText = ocr?.Text ?? string.Empty;
            elements = await _detector.DetectAsync(capture.PngBytes, capture.Width, capture.Height, cancellationToken);
        }
        else
        {
            _logger.LogWarning("[ComputerUse] OCR unavailable — elements will be empty. Install a vision model.");
        }

        var windows = await _controller.ListWindowsAsync(cancellationToken);
        var imagePath = await SaveImageAsync(capture.PngBytes, cancellationToken);

        _logger.LogInformation("[ComputerUse] Observed {W}x{H}, cursor ({X},{Y}), {ElementCount} elements, {WindowCount} windows",
            capture.Width, capture.Height, capture.CursorX, capture.CursorY, elements.Count, windows.Count);

        var observation = new UiObservation(
            capture.Width,
            capture.Height,
            capture.CursorX,
            capture.CursorY,
            ocrText,
            elements,
            windows,
            imagePath);

        // Cache for click_element reuse
        _cache = new CacheEntry(elements, observation, DateTime.UtcNow);
        CleanupOldCaptures();

        return observation;
    }

    public async Task<UiElement?> FindElementAsync(string label, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        var cache = _cache;
        if (cache is not null && (DateTime.UtcNow - cache.Timestamp).TotalSeconds < 5 && cache.Elements.Count > 0)
        {
            var cached = UiElementMatcher.BestMatch(cache.Elements, label);
            if (cached is not null)
            {
                _logger.LogInformation("[ComputerUse] Found '{Label}' in cached elements", label);
                return cached;
            }
        }

        // Fall back to fresh detection
        var elements = await DetectCurrentElementsAsync(cancellationToken);
        return UiElementMatcher.BestMatch(elements, label);
    }

    public async Task<UiActionResult> ClickElementAsync(string label, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default)
    {
        var element = await FindElementAsync(label, cancellationToken);
        if (element is null)
            return new UiActionResult(false, null, $"No UI element found matching '{label}'. Run 'observe' first to list visible elements.", 0, 0);

        var clicked = await _controller.ClickAsync(button, element.CenterX, element.CenterY, cancellationToken);
        return new UiActionResult(
            clicked,
            element,
            clicked
                ? $"Clicked '{element.Label}' ({element.Type}) at ({element.CenterX}, {element.CenterY})"
                : $"Failed to click '{element.Label}'",
            element.CenterX,
            element.CenterY);
    }

    public async Task<UiActionResult> DoubleClickElementAsync(string label, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default)
    {
        var element = await FindElementAsync(label, cancellationToken);
        if (element is null)
            return new UiActionResult(false, null, $"No UI element found matching '{label}'", 0, 0);

        var clicked = await _controller.DoubleClickAsync(button, element.CenterX, element.CenterY, cancellationToken);
        return new UiActionResult(
            clicked,
            element,
            clicked
                ? $"Double-clicked '{element.Label}' ({element.Type}) at ({element.CenterX}, {element.CenterY})"
                : $"Failed to double-click '{element.Label}'",
            element.CenterX,
            element.CenterY);
    }

    public async Task<UiActionResult> TypeIntoElementAsync(string label, string text, CancellationToken cancellationToken = default)
    {
        var element = await FindElementAsync(label, cancellationToken);
        if (element is null)
            return new UiActionResult(false, null, $"No UI element found matching '{label}'", 0, 0);

        var clicked = await _controller.ClickAsync(MouseButton.Left, element.CenterX, element.CenterY, cancellationToken);
        if (!clicked)
            return new UiActionResult(false, element, $"Failed to click '{element.Label}'", element.CenterX, element.CenterY);

        var typed = await _controller.TypeTextAsync(text, cancellationToken);
        return new UiActionResult(
            typed,
            element,
            typed
                ? $"Typed {text.Length} characters into '{element.Label}' at ({element.CenterX}, {element.CenterY})"
                : $"Failed to type into '{element.Label}'",
            element.CenterX,
            element.CenterY);
    }

    private async Task<IReadOnlyList<UiElement>> DetectCurrentElementsAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return Array.Empty<UiElement>();

        var capture = await _controller.CaptureScreenAsync(cancellationToken);
        if (capture is null)
        {
            _logger.LogWarning("[ComputerUse] Screen capture failed");
            return Array.Empty<UiElement>();
        }

        if (!_ocr.OcrAvailable)
        {
            var cached = _cache;
            if (cached is { Elements.Count: > 0 })
            {
                _logger.LogWarning("[ComputerUse] OCR unavailable — returning {Count} STALE cached elements", cached.Elements.Count);
                return cached.Elements;
            }
            _logger.LogWarning("[ComputerUse] OCR unavailable — no cached elements available");
            return Array.Empty<UiElement>();
        }

        return await _detector.DetectAsync(capture.PngBytes, capture.Width, capture.Height, cancellationToken);
    }

    private static async Task<string?> SaveImageAsync(byte[] pngBytes, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "jarvis");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"capture_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
            await File.WriteAllBytesAsync(path, pngBytes, cancellationToken);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CleanupOldCaptures(int keepLast = 10)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis");
        if (!Directory.Exists(dir)) return;
        var files = new DirectoryInfo(dir).GetFiles("capture_*.png")
            .OrderByDescending(f => f.CreationTime).ToList();
        foreach (var f in files.Skip(keepLast))
            try { f.Delete(); } catch { }
    }
}
