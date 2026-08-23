namespace JarvisAI.Application.Agents;

public interface IAutonomousAgentLoop
{
    Task<AutonomousLoopResult> ExecuteAsync(string goal, CancellationToken cancellationToken = default);
}
