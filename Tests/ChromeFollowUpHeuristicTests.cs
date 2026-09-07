using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using JarvisAI.Infrastructure.WebAutomation;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class ChromeFollowUpHeuristicTests
{
    private sealed class MockComputerController : IComputerController
    {
        public bool IsAvailable => true;
        public string? LastKeyPressed { get; private set; }
        public int PressKeyCalls { get; private set; }
        public int FocusCalls { get; private set; }

        public Task<bool> PressKeyAsync(string keyCombination, CancellationToken cancellationToken = default)
        {
            LastKeyPressed = keyCombination;
            PressKeyCalls++;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default)
        {
            var windows = new List<WindowInfo>
            {
                new WindowInfo(1, "YouTube - MrBeast - Chrome", true, true, 0, 0, 1920, 1080)
            };
            return Task.FromResult<IReadOnlyList<WindowInfo>>(windows);
        }

        public Task<bool> FocusWindowAsync(long handle, CancellationToken cancellationToken = default)
        {
            FocusCalls++;
            return Task.FromResult(true);
        }

        public Task<ScreenCapture?> CaptureScreenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ScreenCapture?>(null);
        public Task<bool> MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<bool> ClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<bool> DoubleClickAsync(MouseButton button = MouseButton.Left, int? x = null, int? y = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<bool> ScrollAsync(int deltaY, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<bool> TypeTextAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<string?> GetClipboardAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task<bool> SetClipboardAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private static (ToolRegistry registry, MockComputerController computer) CreateRegistryWithBrowser()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new DateTimeTool());
        registry.Register(new SystemInfoTool());
        var computer = new MockComputerController();
        var browser = new BrowserTool(
            NullLogger<BrowserTool>.Instance,
            new BrowserManager(NullLogger<BrowserManager>.Instance),
            webBrowser: null,
            computer: computer);
        registry.Register(browser);
        return (registry, computer);
    }

    private static AIServiceAdapter CreateAdapter(IAIProvider provider, ToolRegistry registry)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var inner = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        var router = new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance);
        var options = new AIOptions { HiddenPlanningEnabled = false, SelfVerificationEnabled = false };
        return new AIServiceAdapter(inner, provider, router, registry, executor, NullLogger<AIServiceAdapter>.Instance, null!, options: options);
    }

    private static IMemoryService CreateMemoryService()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
    }

    [Theory]
    [InlineData("met en pause", "Space")]
    [InlineData("met en pause le vidéo", "Space")]
    [InlineData("mise en pause", "Space")]
    [InlineData("pause la vidéo", "Space")]
    [InlineData("arrête la vidéo", "Space")]
    [InlineData("stop", "Space")]
    [InlineData("met en play", "Space")]
    [InlineData("reprend", "Space")]
    [InlineData("plein écran", "F")]
    [InlineData("fullscreen", "F")]
    [InlineData("muet", "M")]
    [InlineData("mute", "M")]
    [InlineData("coupe le son", "M")]
    [InlineData("suivant", "Shift+N")]
    [InlineData("prochaine vidéo", "Shift+N")]
    public async Task Heuristic_intercepts_chrome_followup_and_skips_LLM(string message, string expectedKey)
    {
        var llmCalled = false;
        var provider = new MockAIProvider(request =>
        {
            llmCalled = true;
            return AIResponse.Text("should not reach here");
        });

        var (registry, computer) = CreateRegistryWithBrowser();
        var adapter = CreateAdapter(provider, registry);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync(message))
            tokens.Add(token);

        Assert.False(llmCalled, "LLM should NOT be called when heuristic intercepts the message");
        Assert.Equal(expectedKey, computer.LastKeyPressed);
        Assert.Equal(1, computer.PressKeyCalls);
        Assert.Contains("ACTION TERMINÉE", string.Join("", tokens));
    }

    [Fact]
    public async Task Heuristic_does_not_intercept_non_followup_messages()
    {
        var llmCalled = false;
        var provider = new MockAIProvider(request =>
        {
            llmCalled = true;
            return AIResponse.Text("Réponse du LLM.");
        });

        var (registry, _) = CreateRegistryWithBrowser();
        var adapter = CreateAdapter(provider, registry);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("ouvre la dernière vidéo de MrBeast"))
            tokens.Add(token);

        Assert.True(llmCalled, "LLM should be called for non-follow-up messages");
        Assert.Contains("Réponse du LLM.", string.Join("", tokens));
    }

    [Fact]
    public async Task Heuristic_records_task_history()
    {
        var provider = new MockAIProvider(_ => AIResponse.Text("should not reach"));
        var (registry, _) = CreateRegistryWithBrowser();
        var history = new InMemoryTaskExecutionHistory();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var inner = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        var router = new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance);
        var options = new AIOptions { HiddenPlanningEnabled = false, SelfVerificationEnabled = false };
        var adapter = new AIServiceAdapter(inner, provider, router, registry, executor, NullLogger<AIServiceAdapter>.Instance, null!,
            taskHistory: history, options: options);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("plein écran"))
            tokens.Add(token);

        var record = history.GetRecentHistory().Single();
        Assert.True(record.Success);
        Assert.Contains(record.Steps, s => s.StageName == "Thought" && s.Description.Contains("Heuristique", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(record.Steps, s => s.StageName == "Tool" && s.Description.Contains("browser"));
        Assert.Contains(record.Steps, s => s.StageName == "Final");
    }
}
