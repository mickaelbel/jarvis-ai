using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Context;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Planning.Strategies;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Planning;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class AgentOrchestratorTests
{
    private static (AgentOrchestrator orchestrator, InMemoryRunHistory history, ToolRegistry registry) CreateOrchestrator(
        Func<AIRequest, AIResponse>? responder = null,
        ReasoningLoopOptions? loopOptions = null,
        RetryPolicyOptions? retryOptions = null,
        IMemoryService? memoryOverride = null,
        IAIProvider? providerOverride = null)
    {
        var provider = providerOverride ?? new MockAIProvider(responder ?? (_ => AIResponse.Text("ok")));
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var store = new InMemoryMemoryStore();
        var memoryService = memoryOverride ?? new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
        var autoMemory = new AutomaticMemoryService(memoryService, new AutomaticMemoryOptions(), NullLogger<AutomaticMemoryService>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, memoryService, NullLogger<AIService>.Instance);
        var planner = new Planner(aiService, registry, NullLogger<Planner>.Instance);
        var router = new ModelRouter(new ModelRouterOptions("fast", "powerful"), NullLogger<ModelRouter>.Instance);
        var retryPolicy = new RetryPolicy(retryOptions, NullLogger<RetryPolicy>.Instance);
        var strategies = new IPlanningStrategy[]
        {
            new SimplePlanningStrategy(planner, NullLogger<SimplePlanningStrategy>.Instance),
            new ComplexPlanningStrategy(planner, NullLogger<ComplexPlanningStrategy>.Instance),
            new ResearchPlanningStrategy(planner, NullLogger<ResearchPlanningStrategy>.Instance),
            new ComputerUsePlanningStrategy(planner, NullLogger<ComputerUsePlanningStrategy>.Instance),
            new AutomationPlanningStrategy(planner, NullLogger<AutomationPlanningStrategy>.Instance)
        };
        var selector = new PlanningStrategySelector(strategies, NullLogger<PlanningStrategySelector>.Instance);
        var contextBuilder = new ContextBuilder(memoryService, new FakePluginManager(), registry, new FakeSelfImprovementManager(), provider, router, NullLogger<ContextBuilder>.Instance);
        var parallel = new ParallelToolExecutor(executor, registry, retryPolicy, NullLogger<ParallelToolExecutor>.Instance);
        var selection = new ToolSelectionService();
        var loop = new ReasoningLoop(provider, registry, selection, parallel, retryPolicy, autoMemory, loopOptions ?? new ReasoningLoopOptions(), NullLogger<ReasoningLoop>.Instance);
        var history = new InMemoryRunHistory();
        var orchestrator = new AgentOrchestrator(contextBuilder, selector, loop, router, autoMemory, retryPolicy, history, NullLogger<AgentOrchestrator>.Instance);
        return (orchestrator, history, registry);
    }

    private static AgentRequest Request(string goal, ModelSelectionMode mode = ModelSelectionMode.Auto)
        => new() { Goal = goal, Mode = mode };

    [Fact]
    public async Task ExecuteAsync_empty_goal_fails_without_run()
    {
        var (orchestrator, history, _) = CreateOrchestrator(_ => AIResponse.Text("x"));
        var result = await orchestrator.ExecuteAsync(Request("   "));
        Assert.False(result.Success);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public async Task ExecuteAsync_happy_path_succeeds()
    {
        var (orchestrator, history, _) = CreateOrchestrator(_ => AIResponse.Text("tout est fait"));
        var result = await orchestrator.ExecuteAsync(Request("faire le café"));

        Assert.True(result.Success);
        Assert.Equal("tout est fait", result.FinalResponse);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public async Task ExecuteAsync_uses_powerful_model_when_requested()
    {
        var (orchestrator, history, _) = CreateOrchestrator(_ => AIResponse.Text("ok"));
        var result = await orchestrator.ExecuteAsync(Request("réponse courte", ModelSelectionMode.Powerful));

        var run = history.Get(result.RunId);
        Assert.Equal("powerful", run!.Model);
    }

    [Fact]
    public async Task ExecuteAsync_uses_fast_model_when_requested()
    {
        var (orchestrator, history, _) = CreateOrchestrator(_ => AIResponse.Text("ok"));
        var result = await orchestrator.ExecuteAsync(Request("bonjour", ModelSelectionMode.Fast));

        var run = history.Get(result.RunId);
        Assert.Equal("fast", run!.Model);
    }

    [Fact]
    public async Task ExecuteAsync_sets_plan_on_run()
    {
        var (orchestrator, history, _) = CreateOrchestrator(_ => AIResponse.Text("ok"));
        var result = await orchestrator.ExecuteAsync(Request("faire quelque chose"));
        var run = history.Get(result.RunId);

        Assert.NotNull(run!.Plan);
        Assert.Single(run.Plan.Steps);
    }

    [Fact]
    public async Task ExecuteAsync_records_steps_on_run()
    {
        var (orchestrator, history, _) = CreateOrchestrator(_ => AIResponse.Text("ok"));
        var result = await orchestrator.ExecuteAsync(Request("faire quelque chose"));
        var run = history.Get(result.RunId);

        Assert.True(run!.Steps.Count >= 2);
        Assert.Contains(run.Steps, s => s.Kind == RunStepKind.Final);
    }

    [Fact]
    public async Task ExecuteAsync_raises_run_updated_events()
    {
        var (orchestrator, _, _) = CreateOrchestrator(_ => AIResponse.Text("ok"));
        var events = new List<RunStatus>();
        orchestrator.RunUpdated += (_, e) => events.Add(e.Status);

        var result = await orchestrator.ExecuteAsync(Request("tâche"));
        await Task.Delay(20);

        Assert.Contains(RunStatus.Running, events);
        Assert.Contains(RunStatus.Completed, events);
        Assert.Equal(result.RunId, orchestrator.GetRecentRuns(1)[0].RunId);
    }

    [Fact]
    public async Task ExecuteAsync_uses_fallback_plan_on_planning_failure()
    {
        var callCount = 0;
        var (orchestrator, history, _) = CreateOrchestrator(_ =>
        {
            callCount++;
            if (callCount == 1) throw new InvalidOperationException("planning exploded");
            return AIResponse.Text("final ok");
        }, retryOptions: new RetryPolicyOptions { MaxRetries = 0 });

        var result = await orchestrator.ExecuteAsync(Request("faire un truc"));

        Assert.True(result.Success);
        var run = history.Get(result.RunId);
        Assert.Single(run!.Plan!.Steps);
    }

    [Fact]
    public async Task ExecuteAsync_marks_run_failed_when_loop_fails()
    {
        var (orchestrator, history, _) = CreateOrchestrator(_ => AIResponse.Failed("boom"));
        var result = await orchestrator.ExecuteAsync(Request("tâche"));

        Assert.False(result.Success);
        var run = history.Get(result.RunId);
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.NotNull(run.Error);
    }

    [Fact]
    public async Task ExecuteAsync_marks_run_timed_out_when_timeout_exceeded()
    {
        var (orchestrator, history, _) = CreateOrchestrator(providerOverride: new HangingProvider());
        var result = await orchestrator.ExecuteAsync(new AgentRequest { Goal = "tâche", Timeout = TimeSpan.FromMilliseconds(50) });

        Assert.Equal(RunStatus.TimedOut, result.Status);
        var run = history.Get(result.RunId);
        Assert.Equal(RunStatus.TimedOut, run!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_marks_run_cancelled_when_cancellation_requested()
    {
        var (orchestrator, history, _) = CreateOrchestrator(_ => AIResponse.Text("x"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await orchestrator.ExecuteAsync(Request("tâche"), cts.Token);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        var run = history.Get(result.RunId);
        Assert.Equal(RunStatus.Cancelled, run!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_saves_success_outcome_to_memory()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var memoryService = new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
        var (orchestrator, _, _) = CreateOrchestrator(_ => AIResponse.Text("réussite totale"), memoryOverride: memoryService);

        await orchestrator.ExecuteAsync(Request("objectif atteint"));

        var entries = await memoryService.SearchAsync(new MemoryQuery { TextSearch = "SUCCESS" });
        Assert.Single(entries);
        Assert.Equal("agent_observation", entries[0].Category);
    }

    [Fact]
    public async Task ExecuteAsync_uses_parallel_tools_when_allowed()
    {
        var (orchestrator, history, registry) = CreateOrchestrator(_ => AIResponse.Text("ok"));
        registry.Register(new TestTool("system_info", "system info"));

        var result = await orchestrator.ExecuteAsync(new AgentRequest { Goal = "tâche", AllowParallelTools = true });

        Assert.True(result.Success);
        var run = history.Get(result.RunId);
        Assert.NotNull(run);
    }

    [Fact]
    public async Task ExecuteAsync_sets_active_plugins_on_run()
    {
        var (orchestrator, history, _) = CreateOrchestrator(_ => AIResponse.Text("ok"));
        var result = await orchestrator.ExecuteAsync(Request("tâche"));
        var run = history.Get(result.RunId);

        Assert.NotNull(run!.Plugins);
        Assert.Empty(run.Plugins);
    }

    [Fact]
    public async Task GetRun_returns_run_by_id()
    {
        var (orchestrator, _, _) = CreateOrchestrator(_ => AIResponse.Text("ok"));
        var result = await orchestrator.ExecuteAsync(Request("tâche"));
        Assert.NotNull(orchestrator.GetRun(result.RunId));
        Assert.Null(orchestrator.GetRun(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetRecentRuns_returns_newest_first()
    {
        var (orchestrator, _, _) = CreateOrchestrator(_ => AIResponse.Text("ok"));
        var first = await orchestrator.ExecuteAsync(Request("première"));
        var second = await orchestrator.ExecuteAsync(Request("seconde"));

        var recent = orchestrator.GetRecentRuns(2);
        Assert.Equal(second.RunId, recent[0].RunId);
        Assert.Equal(first.RunId, recent[1].RunId);
    }
}
