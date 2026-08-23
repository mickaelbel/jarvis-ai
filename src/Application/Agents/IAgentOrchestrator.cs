namespace JarvisAI.Application.Agents;

public sealed class AgentRunUpdatedEventArgs : EventArgs
{
    public Guid RunId { get; }
    public RunStatus Status { get; }

    public AgentRunUpdatedEventArgs(Guid runId, RunStatus status)
    {
        RunId = runId;
        Status = status;
    }
}

public interface IAgentOrchestrator
{
    event EventHandler<AgentRunUpdatedEventArgs>? RunUpdated;

    Task<OrchestrationResult> ExecuteAsync(AgentRequest request, CancellationToken cancellationToken = default);
    RunRecord? GetRun(Guid runId);
    IReadOnlyList<RunRecord> GetRecentRuns(int count = 20);
}
