using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.ComputerUse;

/// <summary>
/// Orchestrates the computer use pipeline: capture the screen, run OCR, detect UI
/// elements, then act on them by label (observe -> decide -> click/type -> verify).
/// </summary>
public sealed class ComputerUseService : IComputerUseService
{
    private readonly IComputerController _controller;
    private readonly IOcrService _ocr;
    private readonly IUiElementDetector _detector;
    private readonly ILogger<ComputerUseService> _logger;

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

        var windows = await _controller.ListWindowsAsync(cancellationToken);
        var imagePath = await SaveImageAsync(capture.PngBytes, cancellationToken);

        _logger.LogInformation("[ComputerUse] Observed {W}x{H}, cursor ({X},{Y}), {ElementCount} elements, {WindowCount} windows",
            capture.Width, capture.Height, capture.CursorX, capture.CursorY, elements.Count, windows.Count);

        return new UiObservation(
            capture.Width,
            capture.Height,
            capture.CursorX,
            capture.CursorY,
            ocrText,
            elements,
            windows,
            imagePath);
    }

    public async Task<UiElement?> FindElementAsync(string label, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        var elements = await DetectCurrentElementsAsync(cancellationToken);
        return UiElementMatcher.BestMatch(elements, label);
    }

    public async Task<UiActionResult> ClickElementAsync(string label, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default)
    {
        var element = await FindElementAsync(label, cancellationToken);
        if (element is null)
            return new UiActionResult(false, null, $"No UI element found matching '{label}'", 0, 0);

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
        if (capture is null || !_ocr.OcrAvailable)
            return Array.Empty<UiElement>();

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
}
