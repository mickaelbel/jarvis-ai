using JarvisAI.Application.AI;
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

public class PlanningStrategyTests
{
    private static ContextBundle CreateContext(params string[] toolNames)
    {
        var tools = toolNames.Select(n => (ITool)new TestTool(n, $"{n} description", "test")).ToList();
        return new ContextBundle(
            goal: "goal",
            correlationId: Guid.NewGuid(),
            mode: ModelSelectionMode.Auto,
            relevantMemories: Array.Empty<MemoryEntry>(),
            availableTools: tools,
            ollamaStatus: "Ollama is available.",
            sections: Array.Empty<ContextSection>());
    }

    private static (IPlanner planner, ToolRegistry registry) CreatePlanner(IAIProvider provider)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var store = new InMemoryMemoryStore();
        var memory = new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, memory, NullLogger<AIService>.Instance);
        return (new Planner(aiService, registry, NullLogger<Planner>.Instance), registry);
    }

    private static IPlanningStrategySelector CreateSelector()
    {
        var (planner, _) = CreatePlanner(new MockAIProvider(AIResponse.Text("{}")));
        return new PlanningStrategySelector(new IPlanningStrategy[]
        {
            new SimplePlanningStrategy(planner, NullLogger<SimplePlanningStrategy>.Instance),
            new ComplexPlanningStrategy(planner, NullLogger<ComplexPlanningStrategy>.Instance),
            new ResearchPlanningStrategy(planner, NullLogger<ResearchPlanningStrategy>.Instance),
            new ComputerUsePlanningStrategy(planner, NullLogger<ComputerUsePlanningStrategy>.Instance),
            new AutomationPlanningStrategy(planner, NullLogger<AutomationPlanningStrategy>.Instance)
        }, NullLogger<PlanningStrategySelector>.Instance);
    }

    [Theory]
    [InlineData("clique sur le bouton à l'écran", PlanningStrategyKind.ComputerUse)]
    [InlineData("automatiser le rapport quotidien", PlanningStrategyKind.Automation)]
    [InlineData("recherche des informations sur ce sujet", PlanningStrategyKind.Research)]
    [InlineData("bonjour", PlanningStrategyKind.Simple)]
    [InlineData("une demande courte de moins de soixante caracteres", PlanningStrategyKind.Simple)]
    public void Select_maps_keywords_to_strategy(string goal, PlanningStrategyKind expected)
    {
        var selector = CreateSelector();
        Assert.Equal(expected, selector.Select(goal, CreateContext()));
    }

    [Fact]
    public void Select_long_goal_returns_complex()
    {
        var selector = CreateSelector();
        var longGoal = new string('x', 80);
        Assert.Equal(PlanningStrategyKind.Complex, selector.Select(longGoal, CreateContext()));
    }

    [Fact]
    public void Select_is_case_insensitive()
    {
        var selector = CreateSelector();
        Assert.Equal(PlanningStrategyKind.ComputerUse, selector.Select("CLIQUE SUR L'ÉCRAN", CreateContext()));
    }

    [Fact]
    public void Select_prefers_computer_use_over_research()
    {
        var selector = CreateSelector();
        Assert.Equal(PlanningStrategyKind.ComputerUse, selector.Select("recherche puis clique sur l'écran", CreateContext()));
    }

    [Fact]
    public void GetStrategy_returns_registered_strategy()
    {
        var selector = CreateSelector();
        Assert.Equal(PlanningStrategyKind.Simple, selector.GetStrategy(PlanningStrategyKind.Simple).Kind);
        Assert.Equal(PlanningStrategyKind.Complex, selector.GetStrategy(PlanningStrategyKind.Complex).Kind);
        Assert.Equal(PlanningStrategyKind.Research, selector.GetStrategy(PlanningStrategyKind.Research).Kind);
        Assert.Equal(PlanningStrategyKind.ComputerUse, selector.GetStrategy(PlanningStrategyKind.ComputerUse).Kind);
        Assert.Equal(PlanningStrategyKind.Automation, selector.GetStrategy(PlanningStrategyKind.Automation).Kind);
    }

    [Fact]
    public void Strategy_names_follow_convention()
    {
        var selector = CreateSelector();
        Assert.Equal("SimpleStrategy", selector.GetStrategy(PlanningStrategyKind.Simple).Name);
        Assert.Equal("ComplexStrategy", selector.GetStrategy(PlanningStrategyKind.Complex).Name);
    }

    [Fact]
    public async Task Simple_strategy_creates_plan()
    {
        var provider = new MockAIProvider(request =>
        {
            var last = request.Messages.LastOrDefault();
            return AIResponse.Text(
                last is not null && last.Content.Contains("strategy instructions", StringComparison.OrdinalIgnoreCase)
                    ? @"{""goal"": ""simple task"", ""steps"": [{""action"": ""answer"", ""description"": ""answer directly"", ""tool"": null, ""parameters"": {}}]}"
                    : "{}");
        });
        var (planner, _) = CreatePlanner(provider);
        var strategy = new SimplePlanningStrategy(planner, NullLogger<SimplePlanningStrategy>.Instance);

        var plan = await strategy.CreatePlanAsync("simple task", CreateContext("date_time"));

        Assert.NotNull(plan);
        Assert.Single(plan.Steps);
        Assert.Equal("simple task", plan.Goal);
    }

    [Fact]
    public async Task Simple_strategy_falls_back_when_planner_fails()
    {
        var provider = new MockAIProvider(AIResponse.Failed("connection error"));
        var (planner, _) = CreatePlanner(provider);
        var strategy = new SimplePlanningStrategy(planner, NullLogger<SimplePlanningStrategy>.Instance);

        var plan = await strategy.CreatePlanAsync("simple task", CreateContext());

        Assert.Single(plan.Steps);
        Assert.Null(plan.Steps[0].ToolName);
        Assert.Equal(PlanStatus.Created, plan.Status);
    }

    [Fact]
    public async Task Complex_strategy_creates_plan()
    {
        var provider = new MockAIProvider(AIResponse.Text(@"{""goal"": ""complex"", ""steps"": [{""action"": ""a"", ""description"": ""d"", ""tool"": null, ""parameters"": {}}]}"));
        var (planner, _) = CreatePlanner(provider);
        var strategy = new ComplexPlanningStrategy(planner, NullLogger<ComplexPlanningStrategy>.Instance);

        var plan = await strategy.CreatePlanAsync("complex", CreateContext("tool_a"));

        Assert.Single(plan.Steps);
    }

    [Fact]
    public async Task Research_strategy_creates_plan()
    {
        var provider = new MockAIProvider(AIResponse.Text(@"{""goal"": ""research"", ""steps"": [{""action"": ""gather"", ""description"": ""gather info"", ""tool"": null, ""parameters"": {}}]}"));
        var (planner, _) = CreatePlanner(provider);
        var strategy = new ResearchPlanningStrategy(planner, NullLogger<ResearchPlanningStrategy>.Instance);

        var plan = await strategy.CreatePlanAsync("research", CreateContext());

        Assert.Single(plan.Steps);
    }

    [Fact]
    public async Task ComputerUse_strategy_creates_plan()
    {
        var provider = new MockAIProvider(AIResponse.Text(@"{""goal"": ""computer"", ""steps"": [{""action"": ""act"", ""description"": ""act"", ""tool"": null, ""parameters"": {}}]}"));
        var (planner, _) = CreatePlanner(provider);
        var strategy = new ComputerUsePlanningStrategy(planner, NullLogger<ComputerUsePlanningStrategy>.Instance);

        var plan = await strategy.CreatePlanAsync("computer", CreateContext());

        Assert.Single(plan.Steps);
    }

    [Fact]
    public async Task Automation_strategy_creates_plan()
    {
        var provider = new MockAIProvider(AIResponse.Text(@"{""goal"": ""automation"", ""steps"": [{""action"": ""create"", ""description"": ""create script"", ""tool"": null, ""parameters"": {}}]}"));
        var (planner, _) = CreatePlanner(provider);
        var strategy = new AutomationPlanningStrategy(planner, NullLogger<AutomationPlanningStrategy>.Instance);

        var plan = await strategy.CreatePlanAsync("automation", CreateContext());

        Assert.Single(plan.Steps);
    }

    [Fact]
    public async Task Strategy_passes_context_tools_to_planner()
    {
        AIRequest? captured = null;
        var provider = new MockAIProvider(request =>
        {
            captured = request;
            return AIResponse.Text(@"{""goal"": ""g"", ""steps"": [{""action"": ""a"", ""description"": ""d"", ""tool"": null, ""parameters"": {}}]}");
        });
        var (planner, _) = CreatePlanner(provider);
        var strategy = new SimplePlanningStrategy(planner, NullLogger<SimplePlanningStrategy>.Instance);

        await strategy.CreatePlanAsync("goal", CreateContext("date_time", "memory"));

        Assert.NotNull(captured);
        Assert.Contains("date_time", captured!.Messages.Last().Content);
        Assert.Contains("memory", captured.Messages.Last().Content);
    }

    [Fact]
    public async Task Strategy_includes_instructions_in_planner_call()
    {
        AIRequest? captured = null;
        var provider = new MockAIProvider(request =>
        {
            captured = request;
            return AIResponse.Text(@"{""goal"": ""g"", ""steps"": [{""action"": ""a"", ""description"": ""d"", ""tool"": null, ""parameters"": {}}]}");
        });
        var (planner, _) = CreatePlanner(provider);
        var strategy = new SimplePlanningStrategy(planner, NullLogger<SimplePlanningStrategy>.Instance);

        await strategy.CreatePlanAsync("goal", CreateContext());

        Assert.NotNull(captured);
        Assert.Contains("minimal plan", captured!.Messages.Last().Content);
    }

    [Fact]
    public async Task All_strategies_produce_fallback_on_invalid_json()
    {
        var provider = new MockAIProvider(AIResponse.Text("this is not json"));
        var (planner, _) = CreatePlanner(provider);

        var strategies = new IPlanningStrategy[]
        {
            new SimplePlanningStrategy(planner, NullLogger<SimplePlanningStrategy>.Instance),
            new ComplexPlanningStrategy(planner, NullLogger<ComplexPlanningStrategy>.Instance),
            new ResearchPlanningStrategy(planner, NullLogger<ResearchPlanningStrategy>.Instance)
        };

        foreach (var strategy in strategies)
        {
            var plan = await strategy.CreatePlanAsync("goal", CreateContext());
            Assert.Single(plan.Steps);
            Assert.Equal(PlanStatus.Created, plan.Status);
        }
    }
}
