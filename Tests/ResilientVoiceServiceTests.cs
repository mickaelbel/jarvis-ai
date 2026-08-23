using JarvisAI.Application.Voice;
using JarvisAI.Infrastructure.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class ResilientSpeechToTextTests
{
    [Fact]
    public async Task Succeeds_on_first_attempt()
    {
        var fake = new FakeStt("ok");
        var resilient = new ResilientSpeechToTextService(fake, () => Task.FromResult(true), NullLogger<ResilientSpeechToTextService>.Instance);
        var result = await resilient.TranscribeAsync(new byte[] { 1 }, 16000);
        Assert.True(result.Success);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Retries_on_failure()
    {
        var fake = new FakeStt(null); // toujours Failed
        int restartCalls = 0;
        var resilient = new ResilientSpeechToTextService(fake,
            () => { restartCalls++; return Task.FromResult(true); },
            NullLogger<ResilientSpeechToTextService>.Instance,
            maxRetries: 2);
        var result = await resilient.TranscribeAsync(new byte[] { 1 }, 16000);
        Assert.False(result.Success);
        Assert.True(fake.Calls >= 3); // 1 + 2 retries
        Assert.Equal(2, restartCalls); // 2 tentatives de restart
    }

    [Fact]
    public async Task Recovers_when_callback_restarts_engine()
    {
        var fake = new FakeSttWithSequence(new[] { null, null, "ok" });
        var resilient = new ResilientSpeechToTextService(fake, () => Task.FromResult(true), NullLogger<ResilientSpeechToTextService>.Instance, maxRetries: 3);
        var result = await resilient.TranscribeAsync(new byte[] { 1 }, 16000);
        Assert.True(result.Success);
        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        var fake = new FakeStt(null);
        var resilient = new ResilientSpeechToTextService(fake, () => Task.FromResult(true), NullLogger<ResilientSpeechToTextService>.Instance, maxRetries: 5);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resilient.TranscribeAsync(new byte[] { 1 }, 16000, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Reports_failed_after_all_retries()
    {
        var fake = new FakeStt(null);
        var resilient = new ResilientSpeechToTextService(fake, () => Task.FromResult(false), NullLogger<ResilientSpeechToTextService>.Instance, maxRetries: 1);
        var result = await resilient.TranscribeAsync(new byte[] { 1 }, 16000);
        Assert.False(result.Success);
        Assert.Contains("tentative", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Name_is_inherited()
    {
        var fake = new FakeStt("ok");
        var resilient = new ResilientSpeechToTextService(fake, () => Task.FromResult(true), NullLogger<ResilientSpeechToTextService>.Instance);
        Assert.Equal("fake-stt (resilient)", resilient.Name);
    }

    [Fact]
    public async Task IsAvailable_delegates_to_inner()
    {
        var fake = new FakeStt("ok");
        var resilient = new ResilientSpeechToTextService(fake, () => Task.FromResult(true), NullLogger<ResilientSpeechToTextService>.Instance);
        Assert.True(await resilient.IsAvailableAsync());
    }
}

public sealed class ResilientTextToSpeechTests
{
    [Fact]
    public async Task Succeeds_on_first_attempt()
    {
        var primary = new FakeTts(new byte[] { 1, 2, 3 });
        var resilient = new ResilientTextToSpeechService(primary, null, NullLogger<ResilientTextToSpeechService>.Instance);
        var result = await resilient.SynthesizeWavAsync("hello", "default");
        Assert.NotNull(result);
        Assert.Equal(3, result.Length);
    }

    [Fact]
    public async Task Falls_back_on_secondary_when_primary_fails()
    {
        var primary = new FakeTts(null);
        var secondary = new FakeTts(new byte[] { 9 });
        var resilient = new ResilientTextToSpeechService(primary, secondary, NullLogger<ResilientTextToSpeechService>.Instance, maxRetries: 0);
        var result = await resilient.SynthesizeWavAsync("hello", "default");
        Assert.NotNull(result);
        Assert.Equal(9, result[0]);
    }

    [Fact]
    public async Task Retries_then_returns_empty_when_no_fallback()
    {
        var primary = new FakeTts(null);
        var resilient = new ResilientTextToSpeechService(primary, null, NullLogger<ResilientTextToSpeechService>.Instance, maxRetries: 2);
        var result = await resilient.SynthesizeWavAsync("hello", "default");
        Assert.Empty(result);
    }

    [Fact]
    public void Name_includes_resilient()
    {
        var primary = new FakeTts(new byte[] { 1 });
        var resilient = new ResilientTextToSpeechService(primary, null, NullLogger<ResilientTextToSpeechService>.Instance);
        Assert.Equal("fake-tts (resilient)", resilient.Name);
    }

    [Fact]
    public void AvailableVoices_delegates_to_primary()
    {
        var primary = new FakeTts(new byte[] { 1 });
        var resilient = new ResilientTextToSpeechService(primary, null, NullLogger<ResilientTextToSpeechService>.Instance);
        Assert.Contains("default", resilient.AvailableVoices);
    }
}

internal sealed class FakeStt : ISpeechToTextService
{
    private readonly string? _text;
    public int Calls { get; private set; }

    public FakeStt(string? text) { _text = text; }

    public string Name => "fake-stt";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<SttResult> TranscribeAsync(byte[] pcm16, int sampleRate, string? language = null, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(_text is null
            ? SttResult.Failed("fake failure")
            : SttResult.Ok(_text, null, 0));
    }
}

internal sealed class FakeSttWithSequence : ISpeechToTextService
{
    private readonly Queue<string?> _responses;
    public int Calls { get; private set; }

    public FakeSttWithSequence(IEnumerable<string?> responses) { _responses = new Queue<string?>(responses); }

    public string Name => "fake-stt";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<SttResult> TranscribeAsync(byte[] pcm16, int sampleRate, string? language = null, CancellationToken ct = default)
    {
        Calls++;
        var text = _responses.Count > 0 ? _responses.Dequeue() : "ok";
        return Task.FromResult(text is null
            ? SttResult.Failed("fake failure")
            : SttResult.Ok(text, null, 0));
    }
}

internal sealed class FakeTts : ITextToSpeechService
{
    private readonly byte[]? _audio;
    public int Calls { get; private set; }

    public FakeTts(byte[]? audio) { _audio = audio; }

    public string Name => "fake-tts";
    public IReadOnlyList<string> AvailableVoices => new[] { "default" };

    public Task<byte[]> SynthesizeWavAsync(string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(_audio ?? Array.Empty<byte>());
    }
}
