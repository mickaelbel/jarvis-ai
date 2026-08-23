using JarvisAI.Application.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class RetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_succeeds_on_first_attempt()
    {
        var policy = new RetryPolicy();
        var result = await policy.ExecuteAsync(_ => Task.FromResult(42), "op");
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ExecuteAsync_retries_then_succeeds()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions { BaseDelay = TimeSpan.FromMilliseconds(1) });
        var attempts = 0;
        var result = await policy.ExecuteAsync(_ =>
        {
            attempts++;
            return attempts < 3 ? throw new HttpRequestException("connection refused") : Task.FromResult("ok");
        }, "op");
        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_exhausts_retries_and_throws()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions { BaseDelay = TimeSpan.FromMilliseconds(1) });
        var attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new HttpRequestException("connection refused");
        }, "op"));
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_retry_non_retryable_errors()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions { BaseDelay = TimeSpan.FromMilliseconds(1) });
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new InvalidOperationException("purely a bug, not transient");
        }, "op"));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void IsRetryable_returns_true_for_known_keywords()
    {
        var policy = new RetryPolicy();
        Assert.True(policy.IsRetryable(new InvalidOperationException("ollama is unreachable")));
        Assert.True(policy.IsRetryable(new InvalidOperationException("connection refused")));
        Assert.True(policy.IsRetryable(new InvalidOperationException("timed out")));
        Assert.True(policy.IsRetryable(new InvalidOperationException("HTTP 503")));
        Assert.True(policy.IsRetryable(new InvalidOperationException("model not found")));
    }

    [Fact]
    public void IsRetryable_returns_false_for_unknown_message()
    {
        var policy = new RetryPolicy();
        Assert.False(policy.IsRetryable(new InvalidOperationException("completely unrelated failure")));
    }

    [Fact]
    public void IsRetryable_returns_false_for_empty_message()
    {
        var policy = new RetryPolicy();
        Assert.False(policy.IsRetryable(new InvalidOperationException(string.Empty)));
    }

    [Fact]
    public void IsRetryable_timeout_respects_option()
    {
        var on = new RetryPolicy(new RetryPolicyOptions { RetryOnTimeout = true });
        var off = new RetryPolicy(new RetryPolicyOptions { RetryOnTimeout = false });

        Assert.True(on.IsRetryable(new TimeoutException("timeout")));
        Assert.False(off.IsRetryable(new TimeoutException("timeout")));
    }

    [Fact]
    public void IsRetryable_http_respects_option()
    {
        var on = new RetryPolicy(new RetryPolicyOptions { RetryOnHttpErrors = true });
        var off = new RetryPolicy(new RetryPolicyOptions { RetryOnHttpErrors = false });

        Assert.True(on.IsRetryable(new HttpRequestException("503")));
        Assert.False(off.IsRetryable(new HttpRequestException("503")));
    }

    [Fact]
    public void IsRetryable_crash_keyword_respects_option()
    {
        var on = new RetryPolicy(new RetryPolicyOptions { RetryOnCrash = true });
        var off = new RetryPolicy(new RetryPolicyOptions { RetryOnCrash = false });

        Assert.True(on.IsRetryable(new InvalidOperationException("ollama is down")));
        Assert.False(off.IsRetryable(new InvalidOperationException("ollama is down")));
    }

    [Fact]
    public async Task ExecuteAsync_invokes_onRetry_with_attempt_details()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions { BaseDelay = TimeSpan.FromMilliseconds(1) });
        var notified = new List<RetryAttempt>();
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => policy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            throw new HttpRequestException("timeout");
        }, "op", onRetry: notified.Add));

        Assert.Equal(3, notified.Count);
        Assert.Equal(1, notified[0].Attempt);
        Assert.Equal(4, notified[0].TotalAttempts);
        Assert.NotNull(notified[0].Exception);
        Assert.True(notified[0].Delay > TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_never_retries_when_max_retries_is_zero()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions { MaxRetries = 0 });
        var attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new HttpRequestException("timeout");
        }, "op"));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_cancellation_propagates()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions { BaseDelay = TimeSpan.FromMilliseconds(5) });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => policy.ExecuteAsync<int>(
            _ => throw new HttpRequestException("timeout"), "op", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_throws_on_null_operation()
    {
        var policy = new RetryPolicy();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            policy.ExecuteAsync<int>(null!, "op"));
    }

    [Fact]
    public async Task ExecuteAsync_honors_inner_exception_message()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions { MaxRetries = 0 });
        var inner = new Exception("ollama connection timeout");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync<int>(_ =>
            throw new InvalidOperationException("wrapper", inner), "op"));
        Assert.True(policy.IsRetryable(ex));
    }
}
