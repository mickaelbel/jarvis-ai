using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace JarvisAI.Infrastructure.Security;

public interface IToolLockManager
{
    Task<ToolLockHandle> AcquireLockAsync(string resource, string toolName, TimeSpan? timeout = null, CancellationToken ct = default);
    void ReleaseLock(string lockId);
    IReadOnlyList<ToolLockInfo> GetActiveLocks();
}

public sealed class ToolLockManager : IToolLockManager
{
    private readonly ILogger<ToolLockManager> _logger;
    private readonly ConcurrentDictionary<string, ToolLock> _locks = new();

    public ToolLockManager(ILogger<ToolLockManager> logger)
    {
        _logger = logger;
    }

    public async Task<ToolLockHandle> AcquireLockAsync(string resource, string toolName, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var lockId = Guid.NewGuid().ToString("N")[..8];
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var startTime = DateTime.UtcNow;

        while (true)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException();

            if (_locks.TryGetValue(resource, out var existing) && !existing.IsExpired)
            {
                if (DateTime.UtcNow - startTime > effectiveTimeout)
                    throw new TimeoutException($"Lock timeout for resource '{resource}' held by {existing.ToolName}");

                await Task.Delay(50, ct);
                continue;
            }

            var newLock = new ToolLock
            {
                LockId = lockId,
                Resource = resource,
                ToolName = toolName,
                AcquiredAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };

            if (_locks.TryAdd(resource, newLock) || _locks.TryUpdate(resource, newLock, existing!))
            {
                _logger.LogDebug("[ToolLock] Acquired: {Resource} by {Tool}", resource, toolName);
                return new ToolLockHandle(lockId, resource, this);
            }
        }
    }

    public void ReleaseLock(string lockId)
    {
        foreach (var kv in _locks)
        {
            if (kv.Value.LockId == lockId)
            {
                _locks.TryRemove(kv.Key, out _);
                _logger.LogDebug("[ToolLock] Released: {Resource}", kv.Key);
                break;
            }
        }
    }

    public IReadOnlyList<ToolLockInfo> GetActiveLocks()
    {
        return _locks.Values
            .Where(l => !l.IsExpired)
            .Select(l => new ToolLockInfo
            {
                Resource = l.Resource,
                ToolName = l.ToolName,
                AcquiredAt = l.AcquiredAt
            })
            .ToList();
    }
}

internal sealed class ToolLock
{
    public string LockId { get; set; } = "";
    public string Resource { get; set; } = "";
    public string ToolName { get; set; } = "";
    public DateTime AcquiredAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
}

public sealed class ToolLockInfo
{
    public string Resource { get; set; } = "";
    public string ToolName { get; set; } = "";
    public DateTime AcquiredAt { get; set; }
}

public sealed class ToolLockHandle : IDisposable
{
    private readonly IToolLockManager _manager;
    public string LockId { get; }
    public string Resource { get; }

    public ToolLockHandle(string lockId, string resource, IToolLockManager manager)
    {
        LockId = lockId;
        Resource = resource;
        _manager = manager;
    }

    public void Dispose()
    {
        _manager.ReleaseLock(LockId);
    }
}
