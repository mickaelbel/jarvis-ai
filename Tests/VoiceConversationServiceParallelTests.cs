using System.Runtime.CompilerServices;
using JarvisAI.Application.AI;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class VoiceConversationServiceParallelTests
{
    private sealed class FakeStt : ISpeechToTextService
    {
        private readonly string _text;
        public string Name => "fake-stt";
        public int CallCount;
        public TaskCompletionSource SecondCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeStt(string text) => _text = text;

        public Task<SttResult> TranscribeAsync(byte[] pcm16, int sampleRate, string? language = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 2) SecondCallStarted.TrySetResult();
            return Task.FromResult(SttResult.Ok(_text, "fr", 10));
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeTts : ITextToSpeechService
    {
        public string Name => "fake-tts";
        public IReadOnlyList<string> AvailableVoices => Array.Empty<string>();
        public TaskCompletionSource FirstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<TaskCompletionSource> Gates = new();
        public readonly List<CancellationToken> Tokens = new();

        public Task<byte[]> SynthesizeWavAsync(string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Gates) Gates.Add(gate);
            lock (Tokens) Tokens.Add(cancellationToken);
            FirstCallStarted.TrySetResult();

            // Deliberately do NOT observe the token: playback stays blocked until released
            // so the tests can prove STT of the next utterance overlaps TTS playback.
            return Task.Run(async () =>
            {
                await gate.Task;
                return new byte[] { 1, 2, 3 };
            });
        }
    }

    private sealed class FakeAi : IAIService
    {
        private readonly string _response;
        public int CallCount;

        public FakeAi(string response) => _response = response;

        public Task<AIResponse> ChatAsync(string userMessage, AIConversation? conversation = null, string? model = null, ModelSelectionMode mode = ModelSelectionMode.Powerful, CancellationToken cancellationToken = default)
            => Task.FromResult(AIResponse.Text(_response));

        public async IAsyncEnumerable<string> StreamChatAsync(string userMessage, AIConversation? conversation = null, string? model = null, ModelSelectionMode mode = ModelSelectionMode.Powerful, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            yield return _response;
        }
    }

    private sealed class FakeSettingsStore : IVoiceSettingsStore
    {
        private VoiceSettings _settings = new() { WakeWordEnabled = false };
        public VoiceSettings Get() => _settings;
        public void Save(VoiceSettings settings) => _settings = settings;
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public void Register(ITool tool) { }
        public bool Unregister(string name) => false;
        public ITool? GetByName(string name) => null;
        public IReadOnlyList<ITool> GetByCategory(string category) => Array.Empty<ITool>();
        public IReadOnlyList<ITool> GetAll() => Array.Empty<ITool>();
    }

    private static VoiceConversationService CreateService(FakeStt stt, FakeTts tts, FakeAi ai)
    {
        return new VoiceConversationService(
            stt,
            tts,
            ai,
            new EmptyToolRegistry(),
            new FakeSettingsStore(),
            NullLogger<VoiceConversationService>.Instance,
            ttsFallback: null);
    }

    private static async Task WaitAsync(TaskCompletionSource tcs, int seconds = 10)
    {
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public async Task Second_utterance_starts_stt_while_first_tts_is_still_playing()
    {
        var stt = new FakeStt("deuxième commande");
        var tts = new FakeTts();
        var ai = new FakeAi("réponse");
        var service = CreateService(stt, tts, ai);

        var first = service.ProcessUtteranceAsync(new byte[] { 1, 0, 1, 0 });
        await WaitAsync(tts.FirstCallStarted);
        Assert.Single(tts.Tokens);

        var second = service.ProcessUtteranceAsync(new byte[] { 2, 0, 2, 0 });
        await WaitAsync(stt.SecondCallStarted);

        // The gate is released before TTS, so STT#2 runs while TTS#1 is still in flight.
        Assert.False(first.IsCompleted, "The first utterance should still be speaking TTS when STT#2 starts");
        Assert.True(tts.Tokens[0].IsCancellationRequested, "Barge-in should cancel the previous TTS playback");

        tts.Gates[0].TrySetResult();
        await first;

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (tts.Gates) { if (tts.Gates.Count >= 2) break; }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("TTS of the second utterance never started");
            await Task.Delay(25);
        }
        tts.Gates[1].TrySetResult();
        await second;

        Assert.Equal(2, ai.CallCount);
        Assert.Equal(2, stt.CallCount);
        Assert.Equal(2, tts.Tokens.Count);
    }

    [Fact]
    public async Task Interrupt_cancels_tts_playback()
    {
        var stt = new FakeStt("commande");
        var tts = new FakeTts();
        var ai = new FakeAi("réponse");
        var service = CreateService(stt, tts, ai);

        var first = service.ProcessUtteranceAsync(new byte[] { 1, 0, 1, 0 });
        await WaitAsync(tts.FirstCallStarted);
        Assert.False(tts.Tokens[0].IsCancellationRequested);

        service.Interrupt();
        await Task.Delay(100);

        Assert.True(tts.Tokens[0].IsCancellationRequested);

        tts.Gates[0].TrySetResult();
        await first;
        Assert.Equal(1, ai.CallCount);
    }
}
