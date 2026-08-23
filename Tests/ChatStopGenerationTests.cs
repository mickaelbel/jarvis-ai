using JarvisAI.Web.Services;

namespace JarvisAI.Tests;

/// <summary>
/// Unit tests for <see cref="GenerationSession"/>, the per-circuit session that
/// owns the cancellation token used by the chat UI to stop AI generation.
/// </summary>
public class ChatStopGenerationTests
{
    [Fact]
    public void Session_starts_inactive()
    {
        using var session = new GenerationSession();

        Assert.False(session.IsActive);
        Assert.False(session.IsCancellationRequested);
    }

    [Fact]
    public void Start_makes_session_active_with_a_token()
    {
        using var session = new GenerationSession();

        session.Start();

        Assert.True(session.IsActive);
        Assert.False(session.IsCancellationRequested);
        Assert.NotEqual(CancellationToken.None, session.Token);
    }

    [Fact]
    public void Cancel_requests_cancellation_and_marks_inactive()
    {
        using var session = new GenerationSession();
        session.Start();

        session.Cancel();

        Assert.True(session.IsCancellationRequested);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void Token_is_cancelled_after_Cancel()
    {
        using var session = new GenerationSession();
        session.Start();
        var token = session.Token;

        session.Cancel();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_is_safe_before_start()
    {
        using var session = new GenerationSession();

        session.Cancel();

        Assert.False(session.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_is_safe_twice()
    {
        using var session = new GenerationSession();
        session.Start();

        session.Cancel();
        session.Cancel();

        Assert.True(session.IsCancellationRequested);
    }

    [Fact]
    public void Start_after_cancel_resets_the_session()
    {
        using var session = new GenerationSession();
        session.Start();
        session.Cancel();

        session.Start();

        Assert.True(session.IsActive);
        Assert.False(session.IsCancellationRequested);
    }

    [Fact]
    public void Complete_ends_the_session()
    {
        using var session = new GenerationSession();
        session.Start();

        session.Complete();

        Assert.False(session.IsActive);
        Assert.False(session.IsCancellationRequested);
        Assert.Equal(CancellationToken.None, session.Token);
    }

    [Fact]
    public void Dispose_cancels_an_active_session()
    {
        var session = new GenerationSession();
        session.Start();
        var token = session.Token;

        session.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.False(session.IsActive);
    }
}
