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
