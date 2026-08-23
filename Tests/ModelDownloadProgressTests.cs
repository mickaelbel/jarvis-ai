using System.Net;
using System.Text;
using JarvisAI.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// Tests for <see cref="ModelDownloadProgressService"/>: pure formatting/ETAs
/// helpers and the pull lifecycle (start, duplicate rejection, cancel) against
/// a stub Ollama HTTP endpoint.
/// </summary>
public class ModelDownloadProgressTests
{
    private static ModelDownloadProgressService CreateService(Func<CancellationToken, Stream> streamFactory)
    {
        var httpClient = new HttpClient(new StubHttpHandler(streamFactory))
        {
            BaseAddress = new Uri("http://localhost:11434")
        };
        var ollama = new OllamaModelService(httpClient, NullLogger<OllamaModelService>.Instance);
        return new ModelDownloadProgressService(ollama);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
        }
        return condition();
    }

    // ----------------------------------------------------------------
    // Pure computation helpers
    // ----------------------------------------------------------------

    [Fact]
    public void ComputePercent_is_zero_when_total_unknown()
    {
        Assert.Equal(0, ModelDownloadProgressService.ComputePercent(5000, 0));
    }

    [Fact]
    public void ComputePercent_is_clamped_between_0_and_100()
    {
        Assert.Equal(100, ModelDownloadProgressService.ComputePercent(150, 100));
        Assert.Equal(0, ModelDownloadProgressService.ComputePercent(-1, 100));
        Assert.Equal(50, ModelDownloadProgressService.ComputePercent(50, 100));
    }

    [Fact]
    public void ComputeSpeed_converts_bytes_per_elapsed_milliseconds_to_per_second()
    {
        Assert.Equal(1000, ModelDownloadProgressService.ComputeSpeed(1000, 1000));
        Assert.Equal(0, ModelDownloadProgressService.ComputeSpeed(0, 500));
    }

    [Fact]
    public void ComputeEta_is_null_when_unknown_or_zero_speed()
    {
        Assert.Null(ModelDownloadProgressService.ComputeEta(0, 0, 100));
        Assert.Null(ModelDownloadProgressService.ComputeEta(0, 100, 0));
    }

    [Fact]
    public void ComputeEta_is_zero_when_finished()
    {
        Assert.Equal(TimeSpan.Zero, ModelDownloadProgressService.ComputeEta(100, 100, 50));
    }

    [Fact]
    public void ComputeEta_returns_remaining_time()
    {
        var eta = ModelDownloadProgressService.ComputeEta(50, 100, 10);

        Assert.NotNull(eta);
        Assert.Equal(5, eta.Value.TotalSeconds);
    }

    [Fact]
    public void BuildProgressBar_matches_percent()
    {
        Assert.Equal("░░░░░░░░░░", ModelDownloadProgressService.BuildProgressBar(0));
        Assert.Equal("██████████", ModelDownloadProgressService.BuildProgressBar(100));
        Assert.Equal(10, ModelDownloadProgressService.BuildProgressBar(55).Length);
        Assert.Contains("█", ModelDownloadProgressService.BuildProgressBar(55));
    }

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(2L * 1024 * 1024 * 1024, "2.00 GB")]
    public void FormatBytes_uses_appropriate_unit(long bytes, string expected)
    {
        Assert.Equal(expected, ModelDownloadProgressService.FormatBytes(bytes));
    }

    [Fact]
    public void FormatSpeed_uses_appropriate_unit()
    {
        Assert.Equal("512 B/s", ModelDownloadProgressService.FormatSpeed(512));
        Assert.Contains("KB/s", ModelDownloadProgressService.FormatSpeed(2048));
        Assert.Contains("MB/s", ModelDownloadProgressService.FormatSpeed(5L * 1024 * 1024));
    }

    [Theory]
    [InlineData(0.5, "<1s")]
    [InlineData(5, "5s")]
    [InlineData(125, "2m 05s")]
    public void FormatEta_is_human_readable(double seconds, string expected)
    {
        Assert.Equal(expected, ModelDownloadProgressService.FormatEta(TimeSpan.FromSeconds(seconds)));
    }

    // ----------------------------------------------------------------
    // Pull lifecycle against stub Ollama endpoint
    // ----------------------------------------------------------------

    [Fact]
    public async Task StartPullAsync_rejects_blank_model()
    {
        using var service = CreateService(_ => new MemoryStream());

        Assert.False(await service.StartPullAsync("   "));
        Assert.False(await service.StartPullAsync(""));
        Assert.Empty(service.GetAll());
    }

    [Fact]
    public async Task Pull_succeeds_and_flags_state()
    {
        var body = "{\"status\":\"downloading\",\"completed\":50,\"total\":100}\n{\"status\":\"success\"}\n";
        using var service = CreateService(_ => new MemoryStream(Encoding.UTF8.GetBytes(body)));

        var started = await service.StartPullAsync("llama3.1");
        Assert.True(started);

        var state = service.GetState("llama3.1");
        Assert.NotNull(state);
        Assert.True(state!.IsActive);
        Assert.NotNull(state.SizeDisplay);

        var done = await WaitUntilAsync(() => service.GetState("llama3.1") is { IsActive: false });
        var debugState = service.GetState("llama3.1");
        Assert.True(done, $"Pull did not complete in time: active={debugState?.IsActive} error={debugState?.Error} status={debugState?.Status} succeeded={debugState?.Succeeded}");

        var finished = service.GetState("llama3.1")!;
        Assert.True(finished.Succeeded, $"Pull failed: error={finished.Error} status={finished.Status}");
        Assert.Null(finished.Error);
        Assert.Equal("Completed", finished.Status);
        Assert.False(finished.IsActive);
    }

    [Fact]
    public async Task Duplicate_pull_is_rejected_while_running()
    {
        using var service = CreateService(_ => new BlockingPullStream());

        var started = await service.StartPullAsync("dup");
        Assert.True(started);

        var duplicate = await service.StartPullAsync("dup");
        Assert.False(duplicate);

        var active = service.GetActive();
        Assert.Single(active);
        Assert.Equal("dup", active[0].Model);

        service.CancelPull("dup");
        var stopped = await WaitUntilAsync(() => service.GetState("dup") is { IsActive: false });
        Assert.True(stopped, "Pull did not stop in time");

        var state = service.GetState("dup")!;
        Assert.False(state.Succeeded);
        Assert.NotNull(state.Error);
    }

    [Fact]
    public async Task CancelPull_stops_an_active_pull()
    {
        using var service = CreateService(_ => new BlockingPullStream());

        await service.StartPullAsync("slow");

        service.CancelPull("slow");
        var stopped = await WaitUntilAsync(() => service.GetState("slow") is { IsActive: false });
        var s = service.GetState("slow");
        Assert.True(stopped, $"Pull did not stop in time: active={s?.IsActive} err={s?.Error} status={s?.Status}");
        Assert.False(service.GetState("slow")!.Succeeded);
    }

    [Fact]
    public async Task Pull_can_be_restarted_after_completion()
    {
        var body = "{\"status\":\"success\"}\n";
        using var service = CreateService(_ => new MemoryStream(Encoding.UTF8.GetBytes(body)));

        await service.StartPullAsync("again");
        var done = await WaitUntilAsync(() => service.GetState("again") is { IsActive: false });
        Assert.True(done);

        Assert.True(await service.StartPullAsync("again"));
    }

    [Fact]
    public async Task Changed_event_fires_on_progress()
    {
        var body = "{\"status\":\"downloading\",\"completed\":10,\"total\":100}\n{\"status\":\"success\"}\n";
        using var service = CreateService(_ => new MemoryStream(Encoding.UTF8.GetBytes(body)));

        var fired = 0;
        service.Changed += (_, _) => fired++;

        await service.StartPullAsync("evented");

        Assert.True(fired > 0);
    }

    // ----------------------------------------------------------------
    // Test doubles
    // ----------------------------------------------------------------

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Stream> _streamFactory;

        public StubHttpHandler(Func<CancellationToken, Stream> streamFactory) => _streamFactory = streamFactory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StreamContent(_streamFactory(cancellationToken));
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Emits one NDJSON progress line then blocks until the pull's cancellation
    /// token is triggered, simulating an in-flight download.
    /// </summary>
    private sealed class BlockingPullStream : Stream
    {
        private readonly byte[] _firstLine = Encoding.UTF8.GetBytes("{\"status\":\"downloading\",\"completed\":5000,\"total\":10000}\n");
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_pos < _firstLine.Length)
            {
                var n = Math.Min(count, _firstLine.Length - _pos);
                Array.Copy(_firstLine, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => tcs.TrySetResult(0));
            return await tcs.Task;
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
