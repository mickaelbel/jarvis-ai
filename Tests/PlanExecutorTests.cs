using JarvisAI.Application.AI;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Domain.Events.Planning;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using JarvisAI.Infrastructure.Planning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class PlanExecutorTests
{
    private static (PlanExecutor executor, ToolRegistry registry, InMemoryEventBus eventBus) CreateSystem()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var planExecutor = new PlanExecutor(registry, executor, eventBus, NullLogger<PlanExecutor>.Instance);
        return (planExecutor, registry, eventBus);
    }

    private static IMemoryService CreateMemoryService()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_executes_single_tool_step()
    {
        var (planExecutor, registry, _) = CreateSystem();
        registry.Register(new DateTimeTool());

        var plan = new Plan
        {
            Goal = "get time",
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Action = "get time", ToolName = "date_time" }
            }
        };

        var result = await planExecutor.ExecuteAsync(plan);

        Assert.True(result.Success);
        Assert.Equal(1, result.StepsCompleted);
        Assert.Equal(1, result.StepsTotal);
        Assert.Equal(PlanStatus.Completed, plan.Status);
        Assert.Equal(PlanStepStatus.Completed, plan.Steps[0].Status);
        Assert.NotNull(plan.Steps[0].Result);
    }

    [Fact]
    public async Task ExecuteAsync_executes_multiple_steps_sequentially()
    {
        var (planExecutor, registry, _) = CreateSystem();
        registry.Register(new DateTimeTool());
        registry.Register(new SystemInfoTool());

        var plan = new Plan
        {
            Goal = "get info",
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Action = "get time", ToolName = "date_time" },
                new() { Index = 1, Action = "get system", ToolName = "system_info" }
            }
        };

        var result = await planExecutor.ExecuteAsync(plan);

        Assert.True(result.Success);
        Assert.Equal(2, result.StepsCompleted);
        Assert.True(result.TotalDuration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_fails_on_missing_tool()
    {
        var (planExecutor, registry, _) = CreateSystem();

        var plan = new Plan
        {
            Goal = "use missing tool",
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Action = "use tool", ToolName = "nonexistent_tool" }
            }
        };

        var result = await planExecutor.ExecuteAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage);
        Assert.Equal(PlanStatus.Failed, plan.Status);
        Assert.Equal(PlanStepStatus.Failed, plan.Steps[0].Status);
    }

    [Fact]
    public async Task ExecuteAsync_skips_non_tool_steps()
    {
        var (planExecutor, registry, _) = CreateSystem();

        var plan = new Plan
        {
            Goal = "multi step",
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Action = "manual step", ToolName = null },
                new() { Index = 1, Action = "another manual step", ToolName = null }
            }
        };

        var result = await planExecutor.ExecuteAsync(plan);

        Assert.True(result.Success);
        Assert.Equal(2, result.StepsCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_publishes_events()
    {
        var (planExecutor, registry, eventBus) = CreateSystem();
        registry.Register(new DateTimeTool());

        var plan = new Plan
        {
            Goal = "get time",
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Action = "get time", ToolName = "date_time" }
            }
        };

        var events = new List<object>();
        eventBus.Subscribe<PlanCreatedEvent>((e, ct) => { events.Add(e); return Task.CompletedTask; });
        eventBus.Subscribe<PlanStepStartedEvent>((e, ct) => { events.Add(e); return Task.CompletedTask; });
        eventBus.Subscribe<PlanStepCompletedEvent>((e, ct) => { events.Add(e); return Task.CompletedTask; });

        await planExecutor.ExecuteAsync(plan);

        Assert.Equal(3, events.Count);
        Assert.IsType<PlanCreatedEvent>(events[0]);
        Assert.IsType<PlanStepStartedEvent>(events[1]);
        Assert.IsType<PlanStepCompletedEvent>(events[2]);
    }

    [Fact]
    public async Task ExecuteAsync_publishes_failure_event_on_tool_error()
    {
        var (planExecutor, registry, eventBus) = CreateSystem();
        registry.Register(new FailingAITool());

        var plan = new Plan
        {
            Goal = "fail",
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Action = "fail", ToolName = "fail_tool" }
            }
        };

        var events = new List<object>();
        eventBus.Subscribe<PlanFailedEvent>((e, ct) => { events.Add(e); return Task.CompletedTask; });

        await planExecutor.ExecuteAsync(plan);

        Assert.Single(events);
        Assert.IsType<PlanFailedEvent>(events[0]);
    }

    [Fact]
    public async Task ExecuteAsync_stops_on_first_failure()
    {
        var (planExecutor, registry, _) = CreateSystem();
        registry.Register(new FailingAITool());

        var plan = new Plan
        {
            Goal = "multi step fail",
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Action = "fail", ToolName = "fail_tool" },
                new() { Index = 1, Action = "never reached", ToolName = null }
            }
        };

        var result = await planExecutor.ExecuteAsync(plan);

        Assert.False(result.Success);
        Assert.Equal(0, result.StepsCompleted);
        Assert.Equal(PlanStepStatus.Failed, plan.Steps[0].Status);
        Assert.Equal(PlanStepStatus.Pending, plan.Steps[1].Status);
    }

    [Fact]
    public void PlanExecutionResult_success_factory()
    {
        var result = PlanExecutionResult.SuccessResult(Guid.NewGuid(), 3, 3, "all done", TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Equal(3, result.StepsCompleted);
        Assert.Equal(3, result.StepsTotal);
        Assert.Equal("all done", result.Summary);
        Assert.Equal(TimeSpan.FromSeconds(5), result.TotalDuration);
    }

    [Fact]
    public void PlanExecutionResult_failure_factory()
    {
        var result = PlanExecutionResult.FailureResult(Guid.NewGuid(), 1, 3, "tool error", TimeSpan.FromSeconds(2));

        Assert.False(result.Success);
        Assert.Equal(1, result.StepsCompleted);
        Assert.Equal("tool error", result.ErrorMessage);
    }
}

public class PlanRepositoryTests
{
    [Fact]
    public async Task Save_and_retrieve_plan()
    {
        var repo = new PlanRepository(NullLogger<PlanRepository>.Instance);
        var plan = new Plan { Goal = "test" };

        await repo.SaveAsync(plan);
        var retrieved = await repo.GetByIdAsync(plan.Id);

        Assert.NotNull(retrieved);
        Assert.Equal("test", retrieved!.Goal);
    }

    [Fact]
    public async Task GetAllAsync_returns_plans_ordered_by_date()
    {
        var repo = new PlanRepository(NullLogger<PlanRepository>.Instance);
        var plan1 = new Plan { Goal = "first", CreatedAt = DateTime.UtcNow.AddMinutes(-10) };
        var plan2 = new Plan { Goal = "second", CreatedAt = DateTime.UtcNow };

        await repo.SaveAsync(plan1);
        await repo.SaveAsync(plan2);

        var all = await repo.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("second", all[0].Goal);
    }

    [Fact]
    public async Task DeleteAsync_removes_plan()
    {
        var repo = new PlanRepository(NullLogger<PlanRepository>.Instance);
        var plan = new Plan { Goal = "delete me" };

        await repo.SaveAsync(plan);
        var deleted = await repo.DeleteAsync(plan.Id);

        Assert.True(deleted);
        Assert.Null(await repo.GetByIdAsync(plan.Id));
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_nonexistent()
    {
        var repo = new PlanRepository(NullLogger<PlanRepository>.Instance);
        var deleted = await repo.DeleteAsync(Guid.NewGuid());
        Assert.False(deleted);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown()
    {
        var repo = new PlanRepository(NullLogger<PlanRepository>.Instance);
        var plan = await repo.GetByIdAsync(Guid.NewGuid());
        Assert.Null(plan);
    }
}
