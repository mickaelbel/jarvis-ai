using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Tests;

public sealed class ComputerUseSecurityTests
{
    private static (ToolRegistry registry, FakeComputerUseService computerUse, MockConfirmationService confirmation, SecurityManager security) CreateSystem(SecurityOptions? options = null)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var computerUse = new FakeComputerUseService
        {
            ClickResult = new UiActionResult(true, null, "clicked", 130, 210),
            DoubleClickResult = new UiActionResult(true, null, "double-clicked", 130, 210),
            TypeResult = new UiActionResult(true, null, "typed", 0, 0)
        };
        registry.Register(new ComputerUseTool(computerUse, NullLogger<ComputerUseTool>.Instance));
        registry.Register(new ComputerTool(new StubComputerController(), NullLogger<ComputerTool>.Instance));

        var confirmation = new MockConfirmationService
        {
            ResultToReturn = ConfirmationResult.AutoConfirmed()
        };
        var security = new SecurityManager(new Lazy<IToolRegistry>(() => registry), confirmation, eventBus, NullLogger<SecurityManager>.Instance, options);

        return (registry, computerUse, confirmation, security);
    }

    private static AgentContext Context(Dictionary<string, string> args)
        => new("test command", source: "test", new Dictionary<string, object> { ["arguments"] = args });

    private static Dictionary<string, string> Args(params string[] keyValues)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < keyValues.Length; i += 2)
            dict[keyValues[i]] = keyValues[i + 1];
        return dict;
    }

    [Theory]
    [InlineData("computer_use", "click_element")]
    [InlineData("computer_use", "double_click_element")]
    [InlineData("computer_use", "type_into")]
    [InlineData("computer", "click")]
    [InlineData("computer", "double_click")]
    [InlineData("computer", "type_text")]
    [InlineData("computer", "press_key")]
    [InlineData("computer", "close_window")]
    public async Task Destructive_actions_always_request_confirmation(string toolName, string action)
    {
        var (_, _, confirmation, security) = CreateSystem(new SecurityOptions
        {
            RequireConfirmationForHighRisk = false,
            RequireConfirmationForMediumRisk = false,
            AllowDisableConfirmation = true
        });

        var result = await security.CheckAndConfirmAsync(toolName, Context(Args("action", action, "label", "OK")), Args("action", action, "label", "OK"));

        Assert.True(confirmation.WasCalled);
        Assert.True(result.Confirmed);
        Assert.Contains(action, confirmation.LastRequest!.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("computer_use", "observe")]
    [InlineData("computer_use", "find_element")]
    [InlineData("computer", "capture_screen")]
    [InlineData("computer", "list_windows")]
    [InlineData("computer", "move_mouse")]
    [InlineData("computer", "set_clipboard")]
    public async Task Non_destructive_actions_are_not_mandatory(string toolName, string action)
    {
        var (_, _, confirmation, security) = CreateSystem(new SecurityOptions
        {
            RequireConfirmationForHighRisk = false,
            RequireConfirmationForMediumRisk = false,
            AllowDisableConfirmation = false
        });

        var result = await security.CheckAndConfirmAsync(toolName, Context(Args("action", action)), Args("action", action));

        Assert.False(confirmation.WasCalled);
        Assert.True(result.Confirmed);
    }

    [Fact]
    public async Task Destructive_action_denied_by_user_is_not_auto_confirmed()
    {
        var (_, _, confirmation, security) = CreateSystem(new SecurityOptions
        {
            RequireConfirmationForHighRisk = false,
            AllowDisableConfirmation = true
        });
        confirmation.ResultToReturn = ConfirmationResult.Denied(ConfirmationMethod.Text, TimeSpan.FromMilliseconds(5));

        var result = await security.CheckAndConfirmAsync("computer_use", Context(Args("action", "click_element", "label", "OK")), Args("action", "click_element", "label", "OK"));

        Assert.True(confirmation.WasCalled);
        Assert.False(result.Confirmed);
    }

    [Fact]
    public async Task Developer_mode_still_bypasses_non_destructive_high_risk_tools()
    {
        var (_, _, confirmation, security) = CreateSystem(new SecurityOptions
        {
            AllowDisableConfirmation = true,
            RequireConfirmationForHighRisk = true
        });

        var result = await security.CheckAndConfirmAsync("computer_use", Context(Args("action", "observe")), Args("action", "observe"));

        Assert.False(confirmation.WasCalled);
        Assert.True(result.Confirmed);
    }

    [Fact]
    public async Task ToolExecutor_cannot_bypass_destructive_action_confirmation_in_developer_mode()
    {
        var (registry, _, confirmation, security) = CreateSystem(new SecurityOptions
        {
            RequireConfirmationForHighRisk = false,
            RequireConfirmationForMediumRisk = false,
            AllowDisableConfirmation = true
        });
        confirmation.ResultToReturn = ConfirmationResult.Accepted(ConfirmationMethod.Text, TimeSpan.FromMilliseconds(5));
        var executor = new ToolExecutor(registry, new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance), NullLogger<ToolExecutor>.Instance, security);

        var result = await executor.ExecuteAsync("computer_use", Context(Args("action", "click_element", "label", "OK")));

        Assert.True(result.Success);
        Assert.True(confirmation.WasCalled);
    }

    [Fact]
    public async Task ToolExecutor_denied_destructive_action_blocks_execution_in_developer_mode()
    {
        var (registry, _, confirmation, security) = CreateSystem(new SecurityOptions
        {
            RequireConfirmationForHighRisk = false,
            RequireConfirmationForMediumRisk = false,
            AllowDisableConfirmation = true
        });
        confirmation.ResultToReturn = ConfirmationResult.Denied(ConfirmationMethod.Text, TimeSpan.FromMilliseconds(5));
        var executor = new ToolExecutor(registry, new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance), NullLogger<ToolExecutor>.Instance, security);

        var result = await executor.ExecuteAsync("computer_use", Context(Args("action", "click_element", "label", "OK")));

        Assert.False(result.Success);
        Assert.Contains("denied", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.True(confirmation.WasCalled);
    }

    [Fact]
    public void ComputerUseRiskClassifier_classifies_known_actions()
    {
        Assert.Equal(SecurityRiskLevel.Low, ComputerUseRiskClassifier.GetActionRiskLevel("computer_use", "observe"));
        Assert.Equal(SecurityRiskLevel.Low, ComputerUseRiskClassifier.GetActionRiskLevel("computer_use", "find_element"));
        Assert.Equal(SecurityRiskLevel.High, ComputerUseRiskClassifier.GetActionRiskLevel("computer_use", "click_element"));
        Assert.Equal(SecurityRiskLevel.High, ComputerUseRiskClassifier.GetActionRiskLevel("computer_use", "type_into"));
        Assert.Equal(SecurityRiskLevel.Low, ComputerUseRiskClassifier.GetActionRiskLevel("computer", "capture_screen"));
        Assert.Equal(SecurityRiskLevel.Low, ComputerUseRiskClassifier.GetActionRiskLevel("computer", "get_clipboard"));
        Assert.Equal(SecurityRiskLevel.Medium, ComputerUseRiskClassifier.GetActionRiskLevel("computer", "move_mouse"));
        Assert.Equal(SecurityRiskLevel.Medium, ComputerUseRiskClassifier.GetActionRiskLevel("computer", "set_clipboard"));
        Assert.Equal(SecurityRiskLevel.High, ComputerUseRiskClassifier.GetActionRiskLevel("computer", "close_window"));
        Assert.Equal(SecurityRiskLevel.High, ComputerUseRiskClassifier.GetActionRiskLevel("computer", null));
        Assert.Equal(SecurityRiskLevel.High, ComputerUseRiskClassifier.GetActionRiskLevel("computer", "unknown_action"));
        Assert.Equal(SecurityRiskLevel.High, ComputerUseRiskClassifier.GetActionRiskLevel("terminal", "execute_command"));
    }

    [Fact]
    public void ComputerUseRiskClassifier_detects_destructive_actions_only_for_computer_tools()
    {
        Assert.True(ComputerUseRiskClassifier.IsDestructiveAction("computer_use", "click_element"));
        Assert.True(ComputerUseRiskClassifier.IsDestructiveAction("computer_use", "type_into"));
        Assert.True(ComputerUseRiskClassifier.IsDestructiveAction("computer", "close_window"));
        Assert.True(ComputerUseRiskClassifier.IsDestructiveAction("computer", "type_text"));
        Assert.True(ComputerUseRiskClassifier.IsDestructiveAction("computer", "press_key"));
        Assert.False(ComputerUseRiskClassifier.IsDestructiveAction("computer_use", "observe"));
        Assert.False(ComputerUseRiskClassifier.IsDestructiveAction("computer", "capture_screen"));
        Assert.False(ComputerUseRiskClassifier.IsDestructiveAction("computer", "move_mouse"));
        Assert.False(ComputerUseRiskClassifier.IsDestructiveAction("computer", null));
        Assert.False(ComputerUseRiskClassifier.IsDestructiveAction("terminal", "execute_command"));
        Assert.False(ComputerUseRiskClassifier.IsDestructiveAction("file_system", "delete_file"));
    }

    [Fact]
    public async Task WebConfirmationService_uses_dangerous_tool_timeout()
    {
        var store = new MemoryConfirmationStore();
        var options = new SecurityOptions { DangerousToolTimeoutSeconds = 1 };
        var service = new WebConfirmationService(store, NullLogger<WebConfirmationService>.Instance, options);
        var sw = Stopwatch.StartNew();

        var result = await service.RequestConfirmationAsync(
            new ConfirmationRequest("computer_use", "Execute computer_use.click_element", "High", Guid.NewGuid(), Args("action", "click_element")));

        sw.Stop();
        Assert.False(result.Confirmed);
        Assert.Equal("Timed out", result.ResponseText);
        Assert.InRange(sw.ElapsedMilliseconds, 900, 6000);
    }

    [Fact]
    public void TaskExecutionHistory_raises_changed_and_exposes_active_snapshot()
    {
        var history = new InMemoryTaskExecutionHistory();
        var correlationId = Guid.NewGuid();
        var changedCount = 0;
        history.Changed += (_, _) => changedCount++;

        history.StartRecording("hello", correlationId);
        history.AddStep(TaskExecutionStep.Thought("thinking"));
        history.AddStep(TaskExecutionStep.Tool("computer_use", "click_element", 3, true));

        Assert.Equal(3, changedCount);
        var active = history.GetActive();
        Assert.NotNull(active);
        Assert.Equal(correlationId, active.CorrelationId);
        Assert.Equal(2, active.Steps.Count);
        Assert.Null(active.CompletedAt);

        var record = history.Complete("done", true);
        Assert.Equal(4, changedCount);
        Assert.Null(history.GetActive());
        Assert.Same(record, history.GetHistory(correlationId));
        Assert.Equal(2, history.GetRecentHistory(1)[0].Steps.Count);
    }

    [Fact]
    public void TaskExecutionRecord_serializes_to_json()
    {
        var history = new InMemoryTaskExecutionHistory();
        history.StartRecording("hello", Guid.NewGuid());
        history.AddStep(TaskExecutionStep.Thought("thinking"));
        history.AddStep(TaskExecutionStep.Tool("computer_use", "click_element", 3, true, "clicked"));
        var record = history.Complete("done", true);

        var json = JsonSerializer.Serialize(record);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("hello", doc.RootElement.GetProperty("UserMessage").GetString());
        Assert.Equal("done", doc.RootElement.GetProperty("FinalResponse").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("Steps").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("Success").GetBoolean());
    }

    private sealed class StubComputerController : IComputerController
    {
        public bool IsAvailable => true;
        public Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ScreenCapture?>(new ScreenCapture(new byte[] { 1 }, 100, 100, 0, 0));
        public Task<bool> MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
        public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
