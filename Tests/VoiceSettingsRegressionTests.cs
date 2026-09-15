using JarvisAI.Application.AI;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class VoiceSettingsRegressionTests
{
    [Fact]
    public async Task SpeakAsync_UsesConfiguredTtsVoice_NotEngineDefault()
    {
        var tts = new CapturingTts();
        var settings = new VoiceSettings
        {
            WakeWordEnabled = false,
            TtsVoice = "fr-FR-DeniseNeural"
        };
        var svc = new VoiceConversationService(
            new NoOpStt(),
            tts,
            new NoOpAi(),
            new NoTools(),
            new StubSettingsStore(settings),
            NullLogger<VoiceConversationService>.Instance);

        await svc.SpeakAsync("bonjour");

        Assert.Equal("fr-FR-DeniseNeural", tts.LastVoice);
        Assert.NotEqual("fr-FR-HenriNeural", tts.LastVoice);
    }

    [Fact]
    public async Task SpeakAsync_EmptyTtsVoice_CompletesSuccessfully()
    {
        var tts = new CapturingTts();
        var settings = new VoiceSettings
        {
            WakeWordEnabled = false,
            TtsVoice = string.Empty
        };
        var svc = new VoiceConversationService(
            new NoOpStt(),
            tts,
            new NoOpAi(),
            new NoTools(),
            new StubSettingsStore(settings),
            NullLogger<VoiceConversationService>.Instance);

        await svc.SpeakAsync("test");

        Assert.Equal(1, tts.SynthesisCount);
    }

    // ── Minimal fakes ──────────────────────────────────────────────────────

    private sealed class CapturingTts : ITextToSpeechService
    {
        public string Name => "fake-tts";
        public IReadOnlyList<string> AvailableVoices => Array.Empty<string>();
        public string? LastVoice { get; private set; }
        public int SynthesisCount { get; private set; }

        public Task<byte[]> SynthesizeWavAsync(
            string text, string voice, float volume, float speed, CancellationToken ct)
        {
            LastVoice = voice;
            SynthesisCount++;
            return Task.FromResult(new byte[] { 0x52, 0x49 });
        }
    }

    private sealed class StubSettingsStore : IVoiceSettingsStore
    {
        private readonly VoiceSettings _settings;
        public StubSettingsStore(VoiceSettings settings) => _settings = settings;
        public VoiceSettings Get() => _settings;
        public void Save(VoiceSettings settings) { }
    }

    private sealed class NoOpStt : ISpeechToTextService
    {
        public string Name => "noop";
        public Task<SttResult> TranscribeAsync(byte[] pcm, int sr, string? lang, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class NoOpAi : IAIService
    {
        public Task<AIResponse> ChatAsync(
            string msg, AIConversation? conv = null,
            string? model = null, ModelSelectionMode mode = default,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string> BuildSystemPromptWithMemoryAsync(
            IReadOnlyList<AIToolDefinition> tools, CancellationToken ct)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<string> StreamChatAsync(
            string msg, AIConversation? conv = null,
            string? model = null, ModelSelectionMode mode = default,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoTools : IToolRegistry
    {
        public int Version => 0;
        public void Register(ITool tool) { }
        public bool Unregister(string name) => false;
        public ITool? GetByName(string name) => null;
        public IReadOnlyList<ITool> GetByCategory(string c) => Array.Empty<ITool>();
        public IReadOnlyList<ITool> GetAll() => Array.Empty<ITool>();
    }
}
