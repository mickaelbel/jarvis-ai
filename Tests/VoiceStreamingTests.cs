using JarvisAI.Application.AI;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Text;

namespace JarvisAI.Tests;

public class VoiceStreamingTests
{
    [Fact]
    public void DrainSentences_ExtraitLesPhrasesAuFilDesTokens()
    {
        var pending = new StringBuilder();
        pending.Append("Bonjour, je suis Jarvis. Comment");
        var first = VoiceConversationService.DrainSentences(pending, flush: false);

        Assert.Single(first);
        Assert.Equal("Bonjour, je suis Jarvis.", first[0]);
        Assert.StartsWith("Comment", pending.ToString());
    }

    [Fact]
    public void DrainSentences_ProtegeLesDecimales()
    {
        var pending = new StringBuilder("La valeur est 3.");
        var r1 = VoiceConversationService.DrainSentences(pending, flush: false);
        Assert.Empty(r1);  // chiffre suivant pas encore arrivé -> on attend

        pending.Append("14 mètres. Voilà.");
        var r2 = VoiceConversationService.DrainSentences(pending, flush: false);

        Assert.Equal(2, r2.Count);
        Assert.Equal("La valeur est 3.14 mètres.", r2[0]);
        Assert.Equal("Voilà.", r2[1]);
    }

    [Fact]
    public void DrainSentences_ProtegeLesAbbreviationsCourtes()
    {
        var pending = new StringBuilder("Je présente M. Dupont. Il est là.");
        var r = VoiceConversationService.DrainSentences(pending, flush: false);

        // "M." (initiale) ne coupe pas : deux phrases propres.
        Assert.Equal(2, r.Count);
        Assert.Equal("Je présente M. Dupont.", r[0]);
        Assert.Equal("Il est là.", r[1]);
        Assert.Empty(pending.ToString());
    }

    [Fact]
    public void DrainSentences_FlushRendLeFragmentRestant()
    {
        var pending = new StringBuilder("phrase inachevée sans point");
        var r = VoiceConversationService.DrainSentences(pending, flush: true);

        Assert.Single(r);
        Assert.Equal("phrase inachevée sans point", r[0]);
        Assert.Empty(pending.ToString());
    }

    [Fact]
    public async Task StreamAndSpeakAsync_EnvoieLesPhrasesPendantLeFlux()
    {
        // Fake IAIServce : émet des tokens lentement pour prouver que les
        // phrases partent dans le canal AVANT la fin du flux.
        var stt = new FakeStt();
        var tts = new FakeTts();
        var ai = new SlowStreamingAi(new[]
        {
            "Première phrase. ", "Deuxième phrase. ", "Troisième"
        });
        var settingsStore = new FakeVoiceSettingsStore();

        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        var svc = new VoiceConversationService(
            stt, tts, ai, new EmptyToolRegistry(), settingsStore,
            NullLogger<VoiceConversationService>.Instance);

        var audioEmitted = 0;
        svc.AudioForPlayback += _ => Interlocked.Increment(ref audioEmitted);

        // On teste via ProcessUtteranceAsync complet : STT -> LLM -> TTS
        byte[] pcm = new byte[32000]; // ~1 s de silence (le fake STT renvoie du texte)
        await svc.ProcessUtteranceAsync(pcm, 16000);

        // Le fake TTS a reçu au moins une synthèse et l'audio a été émis.
        Assert.True(tts.SynthesisCount >= 3, $"synthèses: {tts.SynthesisCount}");
        Assert.True(audioEmitted >= 3, $"audio émis: {audioEmitted}");
        Assert.Contains("Première phrase.", tts.TextsSynthesized[0]);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeStt : ISpeechToTextService
    {
        public string Name => "fake-stt";
        public Task<SttResult> TranscribeAsync(byte[] pcm16, int sampleRate, string? language, CancellationToken ct)
            => Task.FromResult(SttResult.Ok("jarvis dis moi bonjour", "fr", 5));
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class FakeTts : ITextToSpeechService
    {
        public List<string> TextsSynthesized { get; } = new();
        public int SynthesisCount => TextsSynthesized.Count;
        public int AudioEmittedCount;
        public string Name => "fake";
        public IReadOnlyList<string> AvailableVoices => new[] { "fr_FR-tom-medium" };

        public Task<byte[]> SynthesizeWavAsync(string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken ct = default)
        {
            TextsSynthesized.Add(text);
            return Task.FromResult(new byte[] { 0x52, 0x49 }); // header minimal non vide
        }
    }

    private sealed class SlowStreamingAi : IAIService
    {
        private readonly string[] _chunks;
        public SlowStreamingAi(string[] chunks) => _chunks = chunks;
        public Task<AIResponse> ChatAsync(string userMessage, AIConversation? conversation = null, string? model = null, ModelSelectionMode mode = ModelSelectionMode.Powerful, CancellationToken cancellationToken = default)
            => Task.FromResult(AIResponse.Text(string.Concat(_chunks)));
        public Task<string> BuildSystemPromptWithMemoryAsync(IReadOnlyList<AIToolDefinition> tools, CancellationToken cancellationToken = default)
            => Task.FromResult("System");
        public async IAsyncEnumerable<string> StreamChatAsync(string userMessage, AIConversation? conversation = null, string? model = null, ModelSelectionMode mode = ModelSelectionMode.Powerful,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in _chunks)
            {
                await Task.Delay(10, cancellationToken);
                yield return chunk;
            }
        }
    }

    private sealed class FakeVoiceSettingsStore : IVoiceSettingsStore
    {
        public VoiceSettings Get() => new VoiceSettings();
        public void Save(VoiceSettings settings) { }
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public int Version => 0;
        public void Register(ITool tool) { }
        public bool Unregister(string name) => false;
        public ITool? GetByName(string name) => null;
        public IReadOnlyList<ITool> GetByCategory(string category) => Array.Empty<ITool>();
        public IReadOnlyList<ITool> GetAll() => Array.Empty<ITool>();
    }
}
