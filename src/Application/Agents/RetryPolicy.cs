using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Agents;

public sealed class RetryPolicyOptions
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(3);
    public bool RetryOnTimeout { get; init; } = true;
    public bool RetryOnHttpErrors { get; init; } = true;
    public bool RetryOnCrash { get; init; } = true;
}

public sealed class RetryAttempt
{
    public int Attempt { get; }
    public int TotalAttempts { get; }
    public Exception? Exception { get; }
    public TimeSpan Delay { get; }

    public RetryAttempt(int attempt, int totalAttempts, Exception? exception, TimeSpan delay)
    {
        Attempt = attempt;
        TotalAttempts = totalAttempts;
        Exception = exception;
        Delay = delay;
    }
}

public interface IRetryPolicy
{
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        Action<RetryAttempt>? onRetry = null,
        CancellationToken cancellationToken = default);

    bool IsRetryable(Exception ex);
}

public sealed class RetryPolicy : IRetryPolicy
{
    private readonly RetryPolicyOptions _options;
    private readonly ILogger<RetryPolicy> _logger;

    private static readonly string[] RetryableErrorKeywords =
    {
        "ollama", "connection refused", "connection reset", "timeout", "timed out",
        "unreachable", "503", "502", "504", "429", "internal server error",
        "plugin", "model not found", "could not connect", "network"
    };

    public RetryPolicy(RetryPolicyOptions? options = null, ILogger<RetryPolicy>? logger = null)
    {
        _options = options ?? new RetryPolicyOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RetryPolicy>.Instance;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        Action<RetryAttempt>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));

        var totalAttempts = Math.Max(1, _options.MaxRetries + 1);

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                return await operation(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var isRetryable = IsRetryable(ex);
                if (!isRetryable || attempt >= totalAttempts)
                    throw;

                var delay = ComputeDelay(attempt);
                _logger.LogWarning("[RetryPolicy] Operation '{Operation}' attempt {Attempt}/{Total} failed ({Error}). Retrying in {DelayMs}ms",
                    operationName, attempt, totalAttempts, ex.Message, delay.TotalMilliseconds);

                onRetry?.Invoke(new RetryAttempt(attempt, totalAttempts, ex, delay));

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Operation '{operationName}' did not complete.");
    }

    public bool IsRetryable(Exception ex)
    {
        if (ex is OperationCanceledException or TimeoutException) return _options.RetryOnTimeout;
        if (ex is HttpRequestException) return _options.RetryOnHttpErrors;

        if (!_options.RetryOnCrash) return false;

        var message = ex.InnerException?.Message ?? ex.Message;
        if (string.IsNullOrWhiteSpace(message)) return false;

        return RetryableErrorKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var backoff = TimeSpan.FromMilliseconds(
            Math.Min(_options.MaxDelay.TotalMilliseconds,
                _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
        var jitter = Random.Shared.NextDouble() * 0.5 + 0.75;
        return TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * jitter);
    }
}
