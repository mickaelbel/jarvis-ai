using System.Collections.Concurrent;

namespace JarvisAI.Application.Security;

public sealed class MemoryConfirmationStore : IConfirmationStore
{
    private readonly ConcurrentDictionary<Guid, PendingConfirmation> _requests = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ConfirmationResult>> _waiters = new();

    public Task<Guid> StoreRequestAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid();
        var pending = new PendingConfirmation
        {
            RequestId = requestId,
            Request = request,
            CreatedAt = DateTime.UtcNow,
            IsResolved = false
        };

        _requests[requestId] = pending;
        _waiters[requestId] = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        return Task.FromResult(requestId);
    }

    public Task<ConfirmationRequest?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        _requests.TryGetValue(requestId, out var pending);
        return Task.FromResult(pending?.Request);
    }

    public Task ResolveRequestAsync(Guid requestId, ConfirmationResult result, CancellationToken cancellationToken = default)
    {
        if (_requests.TryGetValue(requestId, out var pending))
        {
            pending.IsResolved = true;
        }

        if (_waiters.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(result);
        }

        return Task.CompletedTask;
    }

    public async Task<ConfirmationResult> WaitForResolutionAsync(Guid requestId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_waiters.TryGetValue(requestId, out var tcs))
        {
            return ConfirmationResult.Denied(ConfirmationMethod.Automatic, TimeSpan.Zero, "Request not found");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return ConfirmationResult.Denied(ConfirmationMethod.Automatic, timeout, "Timed out");
        }
    }

    public Task<IReadOnlyList<PendingConfirmation>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = _requests.Values
            .Where(p => !p.IsResolved)
            .OrderBy(p => p.CreatedAt)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<PendingConfirmation>>(pending);
    }
}
