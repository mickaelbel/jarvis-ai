using JarvisAI.Application.Agents;
using JarvisAI.Application.Planning;

namespace JarvisAI.Tests;

public class RunHistoryTests
{
    [Fact]
    public void CreateRun_stores_record_with_running_status()
    {
        var history = new InMemoryRunHistory();
        var run = history.CreateRun("goal");

        Assert.Equal("goal", run.Goal);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.NotEqual(Guid.Empty, run.RunId);
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Get_returns_null_for_unknown_id()
    {
        var history = new InMemoryRunHistory();
        Assert.Null(history.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Get_returns_record_by_id()
    {
        var history = new InMemoryRunHistory();
        var run = history.CreateRun("goal");

        Assert.Same(run, history.Get(run.RunId));
    }

    [Fact]
    public void GetRecent_returns_most_recent_first()
    {
        var history = new InMemoryRunHistory();
        var first = history.CreateRun("first");
        var second = history.CreateRun("second");
        var third = history.CreateRun("third");

        var recent = history.GetRecent(10);

        Assert.Equal(3, recent.Count);
        Assert.Same(third, recent[0]);
        Assert.Same(second, recent[1]);
        Assert.Same(first, recent[2]);
    }

    [Fact]
    public void GetRecent_respects_count_limit()
    {
        var history = new InMemoryRunHistory();
        for (var i = 0; i < 10; i++) history.CreateRun($"goal {i}");

        Assert.Equal(3, history.GetRecent(3).Count);
        Assert.Equal(10, history.GetRecent(100).Count);
    }

    [Fact]
    public void GetAll_returns_all_records()
    {
        var history = new InMemoryRunHistory();
        history.CreateRun("a");
        history.CreateRun("b");

        Assert.Equal(2, history.GetAll().Count);
    }

    [Fact]
    public void Clear_removes_all_records()
    {
        var history = new InMemoryRunHistory();
        history.CreateRun("a");
        history.Clear();

        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void MarkCompleted_sets_status_final_and_finished_at()
    {
        var run = new RunRecord(Guid.NewGuid(), "goal", DateTimeOffset.UtcNow);
        run.MarkCompleted("done!");

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("done!", run.FinalResponse);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public void MarkFailed_sets_status_and_error()
    {
        var run = new RunRecord(Guid.NewGuid(), "goal", DateTimeOffset.UtcNow);
        run.MarkFailed("boom", "reason");

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("boom", run.Error);
        Assert.Equal("reason", run.Reason);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public void MarkCancelled_sets_status_and_reason()
    {
        var run = new RunRecord(Guid.NewGuid(), "goal", DateTimeOffset.UtcNow);
        run.MarkCancelled("user abort");

        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Equal("user abort", run.Reason);
    }

    [Fact]
    public void MarkTimedOut_sets_status()
    {
        var run = new RunRecord(Guid.NewGuid(), "goal", DateTimeOffset.UtcNow);
        run.MarkTimedOut();

        Assert.Equal(RunStatus.TimedOut, run.Status);
        Assert.NotNull(run.Reason);
    }

    [Fact]
    public void AddStep_increments_index()
    {
        var run = new RunRecord(Guid.NewGuid(), "goal", DateTimeOffset.UtcNow);
        var i0 = run.AddStep(RunStepKind.Thought, "thinking");
        var i1 = run.AddStep(RunStepKind.Final, "done");

        Assert.Equal(0, i0);
        Assert.Equal(1, i1);
        Assert.Equal(2, run.Steps.Count);
    }

    [Fact]
    public void Steps_are_snapshots_of_added_steps()
    {
        var run = new RunRecord(Guid.NewGuid(), "goal", DateTimeOffset.UtcNow);
        run.AddStep(RunStepKind.Thought, "first");
        var snapshot = run.Steps;
        run.AddStep(RunStepKind.Final, "second");

        Assert.Single(snapshot);
        Assert.Equal(2, run.Steps.Count);
    }

    [Fact]
    public void RunRecord_setters_update_state()
    {
        var run = new RunRecord(Guid.NewGuid(), "goal", DateTimeOffset.UtcNow);
        var plan = new Plan { Goal = "goal" };

        run.SetModel("llama3");
        run.SetPlan(plan);
        run.SetPlugins(new[] { "p1", "p2" });
        run.SetIterations(5);
        run.IncrementIterations();
        run.IncrementRetries();
        run.IncrementTools();
        run.AddTokens(10, 20);

        Assert.Equal("llama3", run.Model);
        Assert.Same(plan, run.Plan);
        Assert.Equal(new[] { "p1", "p2" }, run.Plugins);
        Assert.Equal(6, run.Iterations);
        Assert.Equal(1, run.Retries);
        Assert.Equal(1, run.ToolsExecuted);
        Assert.Equal(10, run.PromptTokens);
        Assert.Equal(20, run.CompletionTokens);
    }

    [Fact]
    public void RunRecord_reasoning_trace_and_memory_notes_accumulate()
    {
        var run = new RunRecord(Guid.NewGuid(), "goal", DateTimeOffset.UtcNow);
        run.AddReasoning("line 1");
        run.AddReasoning("line 2");
        run.AddMemoryNote("note 1");

        Assert.Equal(new[] { "line 1", "line 2" }, run.ReasoningTrace);
        Assert.Equal(new[] { "note 1" }, run.MemoryNotes);
    }

    [Fact]
    public void Duration_grows_over_time()
    {
        var run = new RunRecord(Guid.NewGuid(), "goal", DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.True(run.Duration >= TimeSpan.FromSeconds(5));
    }
}
