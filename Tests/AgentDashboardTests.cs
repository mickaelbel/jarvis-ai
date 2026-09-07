using JarvisAI.Application.Agents;
using JarvisAI.Application.Planning;
using JarvisAI.Web.Services;

namespace JarvisAI.Tests;

public class AgentDashboardTests
{
    private static RunRecord CreateRun()
    {
        var run = new RunRecord(Guid.NewGuid(), "objectif", DateTimeOffset.UtcNow.AddSeconds(-30));
        run.SetModel("llama3");
        run.AddStep(RunStepKind.Thought, "je réfléchis");
        run.AddStep(RunStepKind.ToolStarted, "appel", "date_time");
        run.AddStep(RunStepKind.Final, "terminé");
        run.SetPlan(new Plan
        {
            Goal = "objectif",
            Status = PlanStatus.Completed,
            Steps = new List<PlanStep>
            {
                new() { Index = 0, Action = "get time", Description = "récupérer l'heure", ToolName = "date_time", Status = PlanStepStatus.Completed }
            }
        });
        run.AddReasoning("[Thought] Step 1");
        run.AddMemoryNote("note en mémoire");
        run.SetIterations(3);
        run.IncrementRetries();
        run.IncrementTools();
        run.IncrementTools();
        run.AddTokens(100, 200);
        run.MarkCompleted("terminé");
        return run;
    }

    [Fact]
    public void From_maps_basic_fields()
    {
        var dto = AgentRunDto.From(CreateRun());
        Assert.Equal("objectif", dto.Goal);
        Assert.Equal(RunStatus.Completed, dto.Status);
        Assert.Equal("llama3", dto.Model);
        Assert.Equal(3, dto.Iterations);
        Assert.Equal(1, dto.Retries);
        Assert.Equal(2, dto.ToolsExecuted);
        Assert.Equal(100, dto.PromptTokens);
        Assert.Equal(200, dto.CompletionTokens);
        Assert.Equal("terminé", dto.FinalResponse);
    }

    [Fact]
    public void From_maps_duration_seconds()
    {
        var dto = AgentRunDto.From(CreateRun());
        Assert.True(dto.DurationSeconds >= 29);
    }

    [Fact]
    public void From_maps_steps()
    {
        var dto = AgentRunDto.From(CreateRun());
        Assert.Equal(3, dto.Steps.Count);
        Assert.Equal(RunStepKind.Thought, dto.Steps[0].Kind);
        Assert.Equal("date_time", dto.Steps[1].ToolName);
        Assert.Equal("terminé", dto.Steps[2].Content);
    }

    [Fact]
    public void From_maps_plan_steps()
    {
        var dto = AgentRunDto.From(CreateRun());
        Assert.NotNull(dto.PlanSteps);
        var step = Assert.Single(dto.PlanSteps!);
        Assert.Equal("get time", step.Action);
        Assert.Equal("date_time", step.ToolName);
        Assert.Equal(PlanStepStatus.Completed, step.Status);
    }

    [Fact]
    public void From_maps_plugins_and_traces()
    {
        var dto = AgentRunDto.From(CreateRun());
        Assert.Empty(dto.Plugins);
        Assert.Single(dto.ReasoningTrace);
        Assert.Single(dto.MemoryNotes);
    }

    [Fact]
    public void From_handles_null_plan()
    {
        var run = new RunRecord(Guid.NewGuid(), "g", DateTimeOffset.UtcNow);
        var dto = AgentRunDto.From(run);
        Assert.Null(dto.PlanSteps);
        Assert.Empty(dto.Steps);
    }

    [Fact]
    public void From_handles_failed_run()
    {
        var run = new RunRecord(Guid.NewGuid(), "g", DateTimeOffset.UtcNow);
        run.MarkFailed("erreur fatale", "raison");
        var dto = AgentRunDto.From(run);
        Assert.Equal(RunStatus.Failed, dto.Status);
        Assert.Equal("erreur fatale", dto.Error);
        Assert.Equal("raison", dto.Reason);
    }

    [Fact]
    public void From_handles_timed_out_run()
    {
        var run = new RunRecord(Guid.NewGuid(), "g", DateTimeOffset.UtcNow);
        run.MarkTimedOut();
        var dto = AgentRunDto.From(run);
        Assert.Equal(RunStatus.TimedOut, dto.Status);
        Assert.NotNull(dto.FinishedAt);
    }

    [Fact]
    public void From_maps_step_occurred_at_and_duration()
    {
        var run = new RunRecord(Guid.NewGuid(), "g", DateTimeOffset.UtcNow);
        run.AddStep(RunStepKind.ToolCompleted, "resultat", "date_time", true, TimeSpan.FromMilliseconds(42));
        var dto = AgentRunDto.From(run);
        Assert.True(dto.Steps[0].Success);
        Assert.Equal(42, dto.Steps[0].DurationMs);
        Assert.True(dto.Steps[0].OccurredAt != default);
    }

    [Fact]
    public void AgentRunStepDto_roundtrip_via_from()
    {
        var run = new RunRecord(Guid.NewGuid(), "g", DateTimeOffset.UtcNow);
        run.AddStep(RunStepKind.Error, "oops", null, false);
        var dto = AgentRunDto.From(run);
        Assert.Single(dto.Steps);
        Assert.Null(dto.Steps[0].ToolName);
        Assert.False(dto.Steps[0].Success);
    }

    [Fact]
    public void From_maps_run_id_and_started_at()
    {
        var run = CreateRun();
        var dto = AgentRunDto.From(run);
        Assert.Equal(run.RunId, dto.RunId);
        Assert.Equal(run.StartedAt, dto.StartedAt);
    }

    [Fact]
    public void Dto_defaults_are_safe()
    {
        var dto = new AgentRunDto();
        Assert.Equal(Guid.Empty, dto.RunId);
        Assert.Empty(dto.Steps);
        Assert.Empty(dto.Plugins);
        Assert.Equal(RunStatus.Running, dto.Status);
    }
}
