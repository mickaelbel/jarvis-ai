namespace JarvisAI.Application.ComputerUse;

public sealed record UiObservation(
    int ScreenWidth,
    int ScreenHeight,
    int CursorX,
    int CursorY,
    string OcrText,
    IReadOnlyList<UiElement> Elements,
    IReadOnlyList<WindowInfo> Windows,
    string? ImagePath);

public sealed record UiActionResult(
    bool Success,
    UiElement? Element,
    string Message,
    int X,
    int Y);

/// <summary>
/// High-level "computer use" orchestration: capture the screen, detect interactive
/// UI elements, and act on them by label (intelligent click / type).
/// </summary>
public interface IComputerUseService
{
    bool IsAvailable { get; }
    Task<UiObservation?> ObserveAsync(CancellationToken cancellationToken = default);
    Task<UiElement?> FindElementAsync(string label, CancellationToken cancellationToken = default);
    Task<UiActionResult> ClickElementAsync(string label, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default);
    Task<UiActionResult> DoubleClickElementAsync(string label, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default);
    Task<UiActionResult> TypeIntoElementAsync(string label, string text, CancellationToken cancellationToken = default);
    Task<bool> WaitForUiStableAsync(int maxWaitMs = 2500, CancellationToken cancellationToken = default);
}
