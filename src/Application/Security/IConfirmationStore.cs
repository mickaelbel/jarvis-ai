namespace JarvisAI.Application.Security;

public interface IConfirmationStore
{
    Task<Guid> StoreRequestAsync(ConfirmationRequest request, CancellationToken cancellationToken = default);
    Task<ConfirmationRequest?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task ResolveRequestAsync(Guid requestId, ConfirmationResult result, CancellationToken cancellationToken = default);
    Task<ConfirmationResult> WaitForResolutionAsync(Guid requestId, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingConfirmation>> GetPendingAsync(CancellationToken cancellationToken = default);
}

public sealed class PendingConfirmation
{
    public Guid RequestId { get; set; }
    public ConfirmationRequest Request { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsResolved { get; set; }
}
