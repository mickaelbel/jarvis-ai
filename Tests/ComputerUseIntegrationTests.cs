using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Infrastructure.ComputerUse;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace JarvisAI.Tests;

/// <summary>
/// Integration tests: the full computer use pipeline wired together -
/// ToolRegistry + SecurityManager + ToolExecutor + ComputerUseTool/UiElementTool +
/// ComputerUseService + OcrUiElementDetector, with a faked screen/OCR behind it.
/// Proves the observe -> decide -> click/type -> verify loop end to end.
/// </summary>
public sealed class ComputerUseIntegrationTests
{
    private static (ToolRegistry registry, IntegrationFakeController controller, MockConfirmationService confirmation, ToolExecutor executor, InMemoryEventBus eventBus, FakeOcr ocr) CreateSystem(SecurityOptions? options = null)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var controller = new IntegrationFakeController();

        var ocr = new FakeOcr();
        var detector = new OcrUiElementDetector(ocr, NullLogger<OcrUiElementDetector>.Instance);
        var service = new ComputerUseService(controller, ocr, detector, NullLogger<ComputerUseService>.Instance);

        registry.Register(new ComputerUseTool(service, controller, NullLogger<ComputerUseTool>.Instance));
        registry.Register(new UiElementTool(service, NullLogger<UiElementTool>.Instance));
        registry.Register(new ComputerTool(controller, NullLogger<ComputerTool>.Instance));

        var confirmation = new MockConfirmationService
        {
            ResultToReturn = ConfirmationResult.AutoConfirmed()
        };

        var security = new SecurityManager(new Lazy<IToolRegistry>(() => registry), confirmation, eventBus, NullLogger<SecurityManager>.Instance,
            options ?? new SecurityOptions
            {
                RequireConfirmationForHighRisk = true,
                RequireConfirmationForMediumRisk = false,
                AllowDisableConfirmation = false
            });

        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance, security);
        return (registry, controller, confirmation, executor, eventBus, ocr);
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

    private static void SeedScreen(IntegrationFakeController controller, FakeOcr ocr, params OcrWord[] words)
    {
        controller.Screen = new ScreenCapture(new byte[] { 1, 2, 3 }, 1920, 1080, 100, 50);
        ocr.Result = new OcrResult(string.Join(" ", words.Select(w => w.Text)), words);
    }

    [Fact]
    public async Task Observe_runs_through_security_and_executor()
    {
        var (_, controller, confirmation, executor, _, ocr) = CreateSystem();
        SeedScreen(controller, ocr, new OcrWord("OK", 100, 200, 40, 20));
        controller.Windows = new[] { new WindowInfo(1, "Notepad", true, true, 0, 0, 800, 600) };

        var result = await executor.ExecuteAsync("computer_use", Context(Args("action", "observe")));

        Assert.True(result.Success);
        // The whole tool is High risk, so even the read-only observe action is gated.
        Assert.True(confirmation.WasCalled);

        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.Equal(1920, root.GetProperty("screen").GetProperty("ScreenWidth").GetInt32());
        Assert.Equal("OK", root.GetProperty("elements")[0].GetProperty("label").GetString());
        Assert.Equal("Notepad", root.GetProperty("windows")[0].GetProperty("Title").GetString());
    }

    [Fact]
    public async Task Ui_elements_detect_is_low_risk_no_confirmation()
    {
        var (_, controller, confirmation, executor, _, ocr) = CreateSystem();
        SeedScreen(controller, ocr, new OcrWord("Envoyer", 10, 20, 60, 20));

        var result = await executor.ExecuteAsync("ui_elements", Context(Args("action", "detect")));

        Assert.True(result.Success);
        Assert.False(confirmation.WasCalled);

        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal("Envoyer", doc.RootElement.GetProperty("elements")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Click_element_requests_and_accepts_confirmation_then_clicks_center()
    {
        var (_, controller, confirmation, executor, _, ocr) = CreateSystem();
        SeedScreen(controller, ocr, new OcrWord("Envoyer", 100, 200, 60, 20));
        confirmation.ResultToReturn = ConfirmationResult.Accepted(ConfirmationMethod.Text, TimeSpan.FromMilliseconds(5));

        var result = await executor.ExecuteAsync("computer_use", Context(Args("action", "click_element", "label", "Envoyer")));

        Assert.True(result.Success);
        Assert.True(confirmation.WasCalled);
        Assert.Equal(130, controller.LastX);
        Assert.Equal(210, controller.LastY);
    }

    [Fact]
    public async Task Click_element_denied_by_user_blocks_action()
    {
        var (_, controller, confirmation, executor, _, ocr) = CreateSystem();
        SeedScreen(controller, ocr, new OcrWord("Envoyer", 100, 200, 60, 20));
        confirmation.ResultToReturn = ConfirmationResult.Denied(ConfirmationMethod.Text, TimeSpan.FromMilliseconds(5));

        var result = await executor.ExecuteAsync("computer_use", Context(Args("action", "click_element", "label", "Envoyer")));

        Assert.False(result.Success);
        Assert.Contains("denied", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(controller.LastX);
    }

    [Fact]
    public async Task Type_into_click_then_type_roundtrip()
    {
        var (_, controller, confirmation, executor, _, ocr) = CreateSystem();
        SeedScreen(controller, ocr, new OcrWord("input_Email", 100, 200, 120, 24));
        confirmation.ResultToReturn = ConfirmationResult.Accepted(ConfirmationMethod.Text, TimeSpan.FromMilliseconds(5));

        var result = await executor.ExecuteAsync("computer_use",
            Context(Args("action", "type_into", "label", "input_Email", "text", "jarvis@test.fr")));

        Assert.True(result.Success);
        Assert.Equal("jarvis@test.fr", controller.LastText);
        Assert.Equal(160, controller.LastX);
        Assert.Equal(212, controller.LastY);
    }

    [Fact]
    public async Task Multi_step_loop_observe_then_act_using_detected_element()
    {
        // Simulates the autonomous loop: observe the screen, then act on a
        // detected element by label - the exact pattern the LLM loop uses.
        var (_, controller, confirmation, executor, eventBus, ocr) = CreateSystem();
        SeedScreen(controller, ocr, new OcrWord("Envoyer", 100, 200, 60, 20));
        confirmation.ResultToReturn = ConfirmationResult.Accepted(ConfirmationMethod.Text, TimeSpan.FromMilliseconds(5));

        var startedEvents = new List<AgentToolStartedEvent>();
        using (eventBus.Subscribe<AgentToolStartedEvent>((e, _) => { startedEvents.Add(e); return Task.CompletedTask; }))
        {
            var observe = await executor.ExecuteAsync("computer_use", Context(Args("action", "observe")));
            Assert.True(observe.Success);

            var click = await executor.ExecuteAsync("computer_use", Context(Args("action", "click_element", "label", "Envoyer")));
            Assert.True(click.Success);
            Assert.Equal(130, controller.LastX);
            Assert.Equal(210, controller.LastY);

            var type = await executor.ExecuteAsync("computer_use",
                Context(Args("action", "type_into", "label", "Envoyer", "text", "suivi")));
            Assert.True(type.Success);
        }

        Assert.Equal(3, startedEvents.Count);
        Assert.All(startedEvents, e => Assert.Equal("computer_use", e.ToolName));
    }

    [Fact]
    public async Task Whitelist_denial_blocks_computer_use()
    {
        var (registry, controller, _, _, _, ocr) = CreateSystem();
        SeedScreen(controller, ocr, new OcrWord("OK", 100, 200, 40, 20));

        var restrictedOptions = new SecurityOptions
        {
            RequireConfirmationForHighRisk = true,
            AllowDisableConfirmation = false,
            WhitelistedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ui_elements" }
        };
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var confirmation = new MockConfirmationService();
        var security = new SecurityManager(new Lazy<IToolRegistry>(() => registry), confirmation, eventBus, NullLogger<SecurityManager>.Instance, restrictedOptions);
        var restrictedExecutor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance, security);

        var result = await restrictedExecutor.ExecuteAsync("computer_use", Context(Args("action", "click_element", "label", "OK")));

        Assert.False(result.Success);
        Assert.Contains("security", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class IntegrationFakeController : IComputerController
    {
        public bool Available { get; set; } = true;
        public ScreenCapture? Screen { get; set; }
        public int? LastX { get; private set; }
        public int? LastY { get; private set; }
        public MouseButton LastButton { get; private set; }
        public string? LastText { get; private set; }
        public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();

        public bool IsAvailable => Available;

        public Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Available ? Screen : null);

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

        public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default)
        {
            LastText = text;
            return Task.FromResult(true);
        }

        public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Windows);
        public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
