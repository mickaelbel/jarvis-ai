using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Security;

namespace JarvisAI.Tests;

internal sealed class MockConfirmationService : IUserConfirmationService
{
    public ConfirmationResult ResultToReturn { get; set; } = ConfirmationResult.AutoConfirmed();
    public bool WasCalled { get; private set; }
    public ConfirmationRequest? LastRequest { get; private set; }

    public Task<ConfirmationResult> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        LastRequest = request;
        return Task.FromResult(ResultToReturn);
    }
}

internal sealed class FakeComputerUseService : IComputerUseService
{
    public bool Available { get; set; } = true;
    public UiObservation? Observation { get; set; }
    public IReadOnlyList<UiElement> Elements { get; set; } = Array.Empty<UiElement>();
    public UiActionResult ClickResult { get; set; } = new(false, null, "no click", 0, 0);
    public UiActionResult DoubleClickResult { get; set; } = new(false, null, "no double click", 0, 0);
    public UiActionResult TypeResult { get; set; } = new(false, null, "no type", 0, 0);

    public int ClickCalls { get; private set; }
    public int DoubleClickCalls { get; private set; }
    public int TypeCalls { get; private set; }
    public string? LastLabel { get; private set; }
    public string? LastText { get; private set; }
    public MouseButton LastButton { get; private set; }

    public bool IsAvailable => Available;

    public int WaitForUiStableCalls { get; private set; }

    public Task<bool> WaitForUiStableAsync(int maxWaitMs = 2500, CancellationToken cancellationToken = default)
    {
        WaitForUiStableCalls++;
        return Task.FromResult(true);
    }

    public Task<UiObservation?> ObserveAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Available ? Observation : null);

    public Task<UiElement?> FindElementAsync(string label, CancellationToken cancellationToken = default)
    {
        LastLabel = label;
        return Task.FromResult(UiElementMatcher.BestMatch(Elements, label));
    }

    public Task<UiActionResult> ClickElementAsync(string label, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default)
    {
        LastLabel = label;
        LastButton = button;
        ClickCalls++;
        return Task.FromResult(ClickResult);
    }

    public Task<UiActionResult> DoubleClickElementAsync(string label, MouseButton button = MouseButton.Left, CancellationToken cancellationToken = default)
    {
        LastLabel = label;
        LastButton = button;
        DoubleClickCalls++;
        return Task.FromResult(DoubleClickResult);
    }

    public Task<UiActionResult> TypeIntoElementAsync(string label, string text, CancellationToken cancellationToken = default)
    {
        LastLabel = label;
        LastText = text;
        TypeCalls++;
        return Task.FromResult(TypeResult);
    }
}

internal sealed class FakeComputerController : IComputerController
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
        return Task.FromResult(true);
    }

    public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
    {
        LastButton = button;
        LastX = x;
        LastY = y;
        return Task.FromResult(true);
    }

    public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
    {
        LastButton = button;
        LastX = x;
        LastY = y;
        return Task.FromResult(true);
    }

    public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default)
    {
        LastDeltaY = deltaY;
        return Task.FromResult(true);
    }

    public Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default)
    {
        LastText = text;
        return Task.FromResult(true);
    }

    public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default)
    {
        LastKeys = keyCombination;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Windows);

    public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default)
    {
        LastHandle = handle;
        return Task.FromResult(true);
    }

    public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ClipboardText);

    public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        ClipboardText = text;
        return Task.FromResult(true);
    }

    public Task<long> GetForegroundWindowAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ForegroundHandle);
}
