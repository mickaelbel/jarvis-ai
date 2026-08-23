namespace JarvisAI.Application.Agents;

public interface IObservationProvider
{
    Task<string> ObserveAsync(string goal, CancellationToken cancellationToken = default);
}
