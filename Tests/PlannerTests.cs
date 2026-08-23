using JarvisAI.Application.AI;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using JarvisAI.Infrastructure.Planning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class PlannerTests
{
    private static IMemoryService CreateMemoryService()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
    }

    private static (AIService aiService, ToolRegistry registry) CreateAISystem(IAIProvider provider)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        return (aiService, registry);
    }

    [Fact]
    public async Task Planner_creates_plan_from_json_response()
    {
        var planJson = @"{""goal"": ""get time"", ""steps"": [{""action"": ""get current time"", ""description"": ""call date_time tool"", ""tool"": ""date_time"", ""parameters"": {}}]}";
        var provider = new MockAIProvider(AIResponse.Text(planJson));
        var (aiService, registry) = CreateAISystem(provider);
        registry.Register(new DateTimeTool());

        var planner = new Planner(aiService, registry, NullLogger<Planner>.Instance);
        var plan = await planner.CreatePlanAsync("get the current time");

        Assert.NotNull(plan);
        Assert.Equal("get the current time", plan.Goal);
        Assert.Single(plan.Steps);
        Assert.Equal("get current time", plan.Steps[0].Action);
        Assert.Equal("date_time", plan.Steps[0].ToolName);
        Assert.Equal(PlanStatus.Created, plan.Status);
    }

    [Fact]
    public async Task Planner_creates_multi_step_plan()
    {
        var planJson = @"{""goal"": ""research and summarize"", ""steps"": [{""action"": ""search"", ""description"": ""search web"", ""tool"": ""web_search"", ""parameters"": {""query"": ""AI""}}, {""action"": ""summarize"", ""description"": ""summarize results"", ""tool"": null, ""parameters"": {}}]}";
        var provider = new MockAIProvider(AIResponse.Text(planJson));
        var (aiService, registry) = CreateAISystem(provider);

        var planner = new Planner(aiService, registry, NullLogger<Planner>.Instance);
        var plan = await planner.CreatePlanAsync("research and summarize AI");

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("web_search", plan.Steps[0].ToolName);
        Assert.Null(plan.Steps[1].ToolName);
    }

    [Fact]
    public async Task Planner_creates_fallback_plan_on_ai_failure()
    {
        var provider = new MockAIProvider(AIResponse.Failed("connection error"));
        var (aiService, registry) = CreateAISystem(provider);

        var planner = new Planner(aiService, registry, NullLogger<Planner>.Instance);
        var plan = await planner.CreatePlanAsync("do something");

        Assert.NotNull(plan);
        Assert.Single(plan.Steps);
        Assert.Null(plan.Steps[0].ToolName);
    }

    [Fact]
    public async Task Planner_creates_fallback_plan_on_invalid_json()
    {
        var provider = new MockAIProvider(AIResponse.Text("This is not JSON at all, just plain text response."));
        var (aiService, registry) = CreateAISystem(provider);

        var planner = new Planner(aiService, registry, NullLogger<Planner>.Instance);
        var plan = await planner.CreatePlanAsync("do something complex");

        Assert.NotNull(plan);
        Assert.Single(plan.Steps);
        Assert.Null(plan.Steps[0].ToolName);
    }

    [Fact]
    public async Task Planner_parses_parameters_from_json()
    {
        var planJson = @"{""goal"": ""set reminder"", ""steps"": [{""action"": ""set reminder"", ""description"": ""create reminder"", ""tool"": ""reminder"", ""parameters"": {""message"": ""meeting at 3pm"", ""time"": ""15:00""}}]}";
        var provider = new MockAIProvider(AIResponse.Text(planJson));
        var (aiService, registry) = CreateAISystem(provider);

        var planner = new Planner(aiService, registry, NullLogger<Planner>.Instance);
        var plan = await planner.CreatePlanAsync("set a reminder");

        Assert.Single(plan.Steps);
        Assert.Equal("meeting at 3pm", plan.Steps[0].Parameters["message"]);
        Assert.Equal("15:00", plan.Steps[0].Parameters["time"]);
    }

    [Fact]
    public async Task Planner_with_context_uses_provided_tools()
    {
        AIRequest? capturedRequest = null;
        var provider = new MockAIProvider(request =>
        {
            capturedRequest = request;
            return AIResponse.Text(@"{""goal"": ""test"", ""steps"": [{""action"": ""test"", ""description"": ""test"", ""tool"": null, ""parameters"": {}}]}");
        });
        var (aiService, registry) = CreateAISystem(provider);

        var planner = new Planner(aiService, registry, NullLogger<Planner>.Instance);
        await planner.CreatePlanWithContextAsync("test goal", new[] { "tool_a", "tool_b" });

        Assert.NotNull(capturedRequest);
        Assert.Contains("tool_a", capturedRequest!.Messages.Last().Content);
        Assert.Contains("tool_b", capturedRequest.Messages.Last().Content);
    }

    [Fact]
    public void Plan_step_status_starts_as_pending()
    {
        var step = new PlanStep { Index = 0, Action = "test" };
        Assert.Equal(PlanStepStatus.Pending, step.Status);
    }

    [Fact]
    public void Plan_mark_in_progress_sets_status()
    {
        var plan = new Plan { Goal = "test" };
        plan.MarkInProgress();
        Assert.Equal(PlanStatus.InProgress, plan.Status);
    }

    [Fact]
    public void Plan_mark_completed_sets_status_and_time()
    {
        var plan = new Plan { Goal = "test" };
        plan.MarkCompleted("done");
        Assert.Equal(PlanStatus.Completed, plan.Status);
        Assert.NotNull(plan.CompletedAt);
        Assert.Equal("done", plan.Summary);
    }

    [Fact]
    public void Plan_mark_failed_sets_status()
    {
        var plan = new Plan { Goal = "test" };
        plan.MarkFailed("error occurred");
        Assert.Equal(PlanStatus.Failed, plan.Status);
        Assert.Equal("error occurred", plan.Summary);
    }

    [Fact]
    public void Plan_all_steps_completed_returns_true_when_all_done()
    {
        var plan = new Plan
        {
            Goal = "test",
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Status = PlanStepStatus.Completed },
                new() { Index = 1, Status = PlanStepStatus.Completed },
                new() { Index = 2, Status = PlanStepStatus.Skipped }
            }
        };
        Assert.True(plan.AllStepsCompleted);
    }

    [Fact]
    public void Plan_all_steps_completed_returns_false_when_pending()
    {
        var plan = new Plan
        {
            Goal = "test",
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Status = PlanStepStatus.Completed },
                new() { Index = 1, Status = PlanStepStatus.Pending }
            }
        };
        Assert.False(plan.AllStepsCompleted);
    }

    [Fact]
    public async Task Planner_injects_relevant_memories_into_prompt()
    {
        AIRequest? capturedRequest = null;
        var provider = new MockAIProvider(request =>
        {
            capturedRequest = request;
            return AIResponse.Text(@"{""goal"": ""test"", ""steps"": [{""action"": ""test"", ""description"": ""test"", ""tool"": null, ""parameters"": {}}]}");
        });
        var (aiService, registry) = CreateAISystem(provider);

        var memory = CreateMemoryService();
        await memory.SaveAsync("facts.host", "le serveur de prod tourne sous linux", MemoryType.Knowledge, "facts");

        var planner = new Planner(aiService, registry, NullLogger<Planner>.Instance, memory);
        await planner.CreatePlanAsync("déployer sur le serveur");

        Assert.NotNull(capturedRequest);
        Assert.Contains("le serveur de prod tourne sous linux", capturedRequest!.Messages.Last().Content);
    }

    [Fact]
    public async Task Planner_without_memory_still_works()
    {
        var planJson = @"{""goal"": ""test"", ""steps"": [{""action"": ""test"", ""description"": ""test"", ""tool"": null, ""parameters"": {}}]}";
        var provider = new MockAIProvider(AIResponse.Text(planJson));
        var (aiService, registry) = CreateAISystem(provider);

        var planner = new Planner(aiService, registry, NullLogger<Planner>.Instance, memory: null);
        var plan = await planner.CreatePlanAsync("test goal");

        Assert.NotNull(plan);
        Assert.Single(plan.Steps);
    }
}
