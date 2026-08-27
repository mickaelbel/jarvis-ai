using JarvisAI.Application.Agents.Supervision;
using JarvisAI.Application.AI;
using JarvisAI.Application.Context;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class SupervisionVerifierTests
{
    [Fact]
    public void ParseVerdict_verified_true_returns_verified()
    {
        var v = AgentVerifier.ParseVerdict("{\"verified\": true, \"reason\": \"ok\", \"correction\": \"\"}");
        Assert.True(v.Verified);
        Assert.Equal("ok", v.Reason);
        Assert.Null(v.Correction);
    }

    [Fact]
    public void ParseVerdict_not_verified_with_correction()
    {
        var v = AgentVerifier.ParseVerdict("{\"verified\": false, \"reason\": \"missing step\", \"correction\": \"click continue\"}");
        Assert.False(v.Verified);
        Assert.Equal("click continue", v.Correction);
    }

    [Theory]
    [InlineData("pas de json")]
    [InlineData("")]
    [InlineData("{broken")]
    public void ParseVerdict_invalid_is_not_verified(string content)
    {
        var v = AgentVerifier.ParseVerdict(content);
        Assert.False(v.Verified);
    }

    [Fact]
    public void VerifyAsync_calls_provider_and_parses()
    {
        var provider = new MockAIProvider(AIResponse.Text("{\"verified\": true, \"reason\": \"\u00e0 fait\", \"correction\": \"\"}"));
        var verifier = new AgentVerifier(provider, NullLogger<AgentVerifier>.Instance);
        var verdict = verifier.VerifyAsync("goal", "action out", "observation").GetAwaiter().GetResult();
        Assert.True(verdict.Verified);
    }
}

public class AgentTaskGraphTests
{
    [Fact]
    public void GetReady_returns_tasks_with_satisfied_dependencies()
    {
        var graph = new AgentTaskGraph();
        var a = graph.Add("A", "goal A");
        var b = graph.Add("B", "goal B", a.Id);
        var c = graph.Add("C", "goal C", b.Id);

        var ready0 = graph.GetReady();
        Assert.Single(ready0);
        Assert.Equal(a.Id, ready0[0].Id);

        a.MarkCompleted("r");
        var ready1 = graph.GetReady();
        Assert.Single(ready1);
        Assert.Equal(b.Id, ready1[0].Id);

        b.MarkCompleted();
        var ready2 = graph.GetReady();
        Assert.Single(ready2);
        Assert.Equal(c.Id, ready2[0].Id);
    }

    [Fact]
    public void AbortBlocked_marks_tasks_whose_dependency_failed()
    {
        var graph = new AgentTaskGraph();
        var a = graph.Add("A", "goal A");
        var b = graph.Add("B", "goal B", a.Id);
        var c = graph.Add("C", "goal C", b.Id);

        a.MarkFailed("boom");
        graph.AbortBlocked();

        Assert.Equal(AgentTaskStatus.Aborted, b.Status);
        Assert.Equal(AgentTaskStatus.Aborted, c.Status);
    }

    [Fact]
    public void AllCompleted_is_false_while_tasks_remain()
    {
        var graph = new AgentTaskGraph();
        graph.Add("A", "goal A");
        graph.Add("B", "goal B");
        Assert.False(graph.AllCompleted);
        foreach (var t in graph.Tasks) t.MarkCompleted();
        Assert.True(graph.AllCompleted);
    }
}

public class TaskExecutorTests
{
    [Fact]
    public async Task Retries_then_succeeds()
    {
        var graph = new AgentTaskGraph();
        var task = graph.Add("T", "goal");
        var attempts = 0;
        var executor = new TaskExecutor(NullLogger<TaskExecutor>.Instance);

        await executor.RunAsync(graph, async (t, ct) =>
        {
            attempts++;
            if (attempts < 3) return (false, (string?)null, (string?)"transient");
            return (true, (string?)"done", (string?)null);
        }, parallelize: false);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(3, attempts);
        Assert.Equal("done", task.Result);
    }

    [Fact]
    public async Task Gives_up_after_max_retries_and_marks_failed()
    {
        var graph = new AgentTaskGraph();
        var task = graph.Add("T", "goal");
        task.MaxRetries = 2;
        var executor = new TaskExecutor(NullLogger<TaskExecutor>.Instance);

        await executor.RunAsync(graph, static (_, _) => Task.FromResult<(bool, string?, string?)>((false, null, "permanent failure")), parallelize: false);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(3, task.RetryCount);
    }

    [Fact]
    public async Task Parallel_independent_tasks_all_complete()
    {
        var graph = new AgentTaskGraph();
        var a = graph.Add("A", "goal A");
        var b = graph.Add("B", "goal B");
        graph.Add("C", "goal C");
        var executor = new TaskExecutor(NullLogger<TaskExecutor>.Instance);

        await executor.RunAsync(graph, static (t, _) => Task.FromResult<(bool, string?, string?)>((true, $"result {t.Id}", null)), parallelize: true);

        Assert.All(graph.Tasks, t => Assert.Equal(AgentTaskStatus.Completed, t.Status));
        Assert.Equal(3, graph.Tasks.Count);
    }

    [Fact]
    public async Task Blocked_by_failed_dependency_aborts_downstream()
    {
        var graph = new AgentTaskGraph();
        var a = graph.Add("A", "goal A");
        var b = graph.Add("B", "goal B", a.Id);
        var executor = new TaskExecutor(NullLogger<TaskExecutor>.Instance);

        await executor.RunAsync(graph, (t, _) =>
        {
            if (t.Id == a.Id) return Task.FromResult<(bool, string?, string?)>((false, null, "fail"));
            return Task.FromResult<(bool, string?, string?)>((true, "unexpected", null));
        }, parallelize: false);

        Assert.Equal(AgentTaskStatus.Failed, a.Status);
        Assert.Equal(AgentTaskStatus.Aborted, b.Status);
    }
}

public class SpecializedAgentSelectorTests
{
    private static SpecializedAgentSelector CreateSelector()
        => new();

    private static ContextBundle BundleWith(params string[] toolCategories)
    {
        var tools = toolCategories
            .Select(c => (ITool)new TestTool($"{c}_tool", $"{c} tool", category: c))
            .ToList();
        return new ContextBundle(
            goal: "goal",
            correlationId: Guid.NewGuid(),
            mode: JarvisAI.Application.AI.ModelSelectionMode.Auto,
            relevantMemories: Array.Empty<MemoryEntry>(),
            activePlugins: Array.Empty<string>(),
            availableTools: tools,
            ollamaStatus: "available",
            sections: Array.Empty<ContextSection>());
    }

    [Fact]
    public void Select_coding_when_code_keywords_and_terminal_available()
    {
        var bundle = BundleWith("terminal", "filesystem");
        var agent = CreateSelector().Select("écris un script python pour trier des fichiers", bundle);
        Assert.Equal(AgentKind.Coding, agent.Kind);
    }

    [Fact]
    public void Select_computer_when_ui_keywords_and_computer_use_available()
    {
        var bundle = BundleWith("computer_use", "vision");
        var agent = CreateSelector().Select("clique sur le bouton et navigue", bundle);
        Assert.Equal(AgentKind.Computer, agent.Kind);
    }

    [Fact]
    public void Select_research_when_search_keywords()
    {
        var bundle = BundleWith("web", "browser");
        var agent = CreateSelector().Select("recherche les meilleures coques carbone", bundle);
        Assert.Equal(AgentKind.Research, agent.Kind);
    }

    [Fact]
    public void Select_general_for_neutral_goal()
    {
        var bundle = BundleWith("system");
        var agent = CreateSelector().Select("donne-moi l'heure", bundle);
        Assert.Equal(AgentKind.General, agent.Kind);
    }
}
