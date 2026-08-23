using JarvisAI.Application.AI;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Reasoning;
using JarvisAI.Application.Planning;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using JarvisAI.Infrastructure.Planning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class ReasoningEngineTests
{
    private static IMemoryService CreateMemoryService()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
    }

    private static AIService CreateAIService(IAIProvider provider)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        return new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
    }

    [Fact]
    public async Task AnalyzeGoalAsync_creates_initial_thought()
    {
        var thoughtJson = @"{""type"": ""Analysis"", ""content"": ""Breaking down the goal"", ""conclusion"": ""Need tools A and B""}";
        var provider = new MockAIProvider(AIResponse.Text(thoughtJson));
        var aiService = CreateAIService(provider);

        var engine = new ReasoningEngine(aiService, NullLogger<ReasoningEngine>.Instance);
        var context = await engine.AnalyzeGoalAsync("do something", new[] { "tool_a", "tool_b" });

        Assert.Equal("do something", context.Goal);
        Assert.Single(context.Thoughts);
        Assert.Equal(ThoughtType.Analysis, context.Thoughts[0].Type);
        Assert.Equal("Breaking down the goal", context.Thoughts[0].Content);
        Assert.Equal("Need tools A and B", context.Thoughts[0].Conclusion);
    }

    [Fact]
    public async Task NextThoughtAsync_appends_thought_to_context()
    {
        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
                return AIResponse.Text(@"{""type"": ""Analysis"", ""content"": ""initial analysis"", ""conclusion"": null}");
            return AIResponse.Text(@"{""type"": ""Decision"", ""content"": ""choosing approach"", ""conclusion"": ""use tool_a""}");
        });
        var aiService = CreateAIService(provider);

        var engine = new ReasoningEngine(aiService, NullLogger<ReasoningEngine>.Instance);
        var context = await engine.AnalyzeGoalAsync("goal", new[] { "tool_a" });
        var thought = await engine.NextThoughtAsync(context);

        Assert.Equal(2, context.Thoughts.Count);
        Assert.Equal(ThoughtType.Decision, thought.Type);
        Assert.Equal("choosing approach", thought.Content);
    }

    [Fact]
    public async Task NextThoughtAsync_marks_complete_on_conclusion()
    {
        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
                return AIResponse.Text(@"{""type"": ""Analysis"", ""content"": ""thinking"", ""conclusion"": null}");
            return AIResponse.Text(@"{""type"": ""Conclusion"", ""content"": ""final answer"", ""conclusion"": ""plan is ready""}");
        });
        var aiService = CreateAIService(provider);

        var engine = new ReasoningEngine(aiService, NullLogger<ReasoningEngine>.Instance);
        var context = await engine.AnalyzeGoalAsync("goal", Array.Empty<string>());
        await engine.NextThoughtAsync(context);

        Assert.True(context.IsComplete);
        Assert.Equal("plan is ready", context.CurrentConclusion);
    }

    [Fact]
    public async Task GeneratePlanFromReasoningAsync_creates_plan()
    {
        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
                return AIResponse.Text(@"{""type"": ""Analysis"", ""content"": ""thinking"", ""conclusion"": null}");
            return AIResponse.Text(@"{""goal"": ""test"", ""steps"": [{""action"": ""step1"", ""description"": ""first step"", ""tool"": ""tool_a"", ""parameters"": {}}]}");
        });
        var aiService = CreateAIService(provider);

        var engine = new ReasoningEngine(aiService, NullLogger<ReasoningEngine>.Instance);
        var context = await engine.AnalyzeGoalAsync("test goal", new[] { "tool_a" });

        var plan = await engine.GeneratePlanFromReasoningAsync(context);

        Assert.NotNull(plan);
        Assert.Equal("test goal", plan.Goal);
        Assert.Single(plan.Steps);
        Assert.Equal("tool_a", plan.Steps[0].ToolName);
    }

    [Fact]
    public async Task GeneratePlanFromReasoningAsync_creates_fallback_on_failure()
    {
        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
                return AIResponse.Text(@"{""type"": ""Analysis"", ""content"": ""thinking"", ""conclusion"": null}");
            return AIResponse.Failed("error");
        });
        var aiService = CreateAIService(provider);

        var engine = new ReasoningEngine(aiService, NullLogger<ReasoningEngine>.Instance);
        var context = await engine.AnalyzeGoalAsync("test goal", new[] { "tool_a" });

        var plan = await engine.GeneratePlanFromReasoningAsync(context);

        Assert.NotNull(plan);
        Assert.Single(plan.Steps);
        Assert.Equal("test goal", plan.Goal);
    }

    [Fact]
    public async Task AnalyzeGoalAsync_handles_ai_failure_gracefully()
    {
        var provider = new MockAIProvider(AIResponse.Failed("connection error"));
        var aiService = CreateAIService(provider);

        var engine = new ReasoningEngine(aiService, NullLogger<ReasoningEngine>.Instance);
        var context = await engine.AnalyzeGoalAsync("goal", Array.Empty<string>());

        Assert.Single(context.Thoughts);
        Assert.Contains("Unable to process", context.Thoughts[0].Content);
    }

    [Fact]
    public void ThoughtStep_starts_at_index_zero()
    {
        var step = new ThoughtStep { Type = ThoughtType.Analysis, Content = "test" };
        Assert.Equal(0, step.Index);
    }

    [Fact]
    public void ReasoningContext_addThought_increments_index()
    {
        var context = new ReasoningContext { Goal = "test" };
        context.AddThought(new ThoughtStep { Type = ThoughtType.Analysis, Content = "first" });
        context.AddThought(new ThoughtStep { Type = ThoughtType.Decision, Content = "second" });

        Assert.Equal(2, context.Thoughts.Count);
        Assert.Equal(0, context.Thoughts[0].Index);
        Assert.Equal(1, context.Thoughts[1].Index);
    }

    [Fact]
    public void ReasoningContext_getReasoningTrace_formats_thoughts()
    {
        var context = new ReasoningContext { Goal = "test" };
        context.AddThought(new ThoughtStep { Type = ThoughtType.Analysis, Content = "step one" });
        context.AddThought(new ThoughtStep { Type = ThoughtType.Decision, Content = "step two", Conclusion = "chosen" });

        var trace = context.GetReasoningTrace();

        Assert.Contains("[Analysis] Step 0: step one", trace);
        Assert.Contains("[Decision] Step 1: step two -> chosen", trace);
    }
}
