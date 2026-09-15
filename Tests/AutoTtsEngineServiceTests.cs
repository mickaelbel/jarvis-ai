using JarvisAI.Application.Voice;
using JarvisAI.Infrastructure.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// Tests de l'auto-détection réelle du moteur TTS : le mapping n'est plus figé
/// (edge → piper → windows) mais choisi par la voix demandée et la disponibilité
/// réelle des moteurs, relue à chaque appel.
/// </summary>
public class AutoTtsEngineServiceTests
{
    private sealed class FakeEngine : ITextToSpeechService
    {
        public string Name { get; }
        public IReadOnlyList<string> AvailableVoices { get; }
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public string? LastVoice { get; private set; }

        public FakeEngine(string name, params string[] voices)
        {
            Name = name;
            AvailableVoices = voices;
        }

        public Task<byte[]> SynthesizeWavAsync(string text, string voice, float volume, float speed, CancellationToken ct)
        {
            Calls++;
            LastVoice = voice;
            if (Fail) throw new InvalidOperationException($"échec {Name}");
            return Task.FromResult(new byte[] { 0x52, 0x49 });
        }
    }

    private sealed class StubStore : IVoiceSettingsStore
    {
        public VoiceSettings Settings { get; set; } = new();
        public VoiceSettings Get() => Settings;
        public void Save(VoiceSettings settings) => Settings = settings;
    }

    private static AutoTtsEngineService Create(StubStore store, params FakeEngine[] engines)
        => new(engines, store, NullLogger<AutoTtsEngineService>.Instance);

    [Fact]
    public async Task Choisit_le_moteur_dont_la_liste_contient_la_voix()
    {
        var edge = new FakeEngine("EdgeTTS", "fr-FR-HenriNeural");
        var piper = new FakeEngine("Piper", "fr_FR-upmc-medium");
        var auto = Create(new StubStore(), edge, piper);

        await auto.SynthesizeWavAsync("bonjour", "fr_FR-upmc-medium");

        Assert.Equal(1, piper.Calls);
        Assert.Equal(0, edge.Calls);
        Assert.Equal("fr_FR-upmc-medium", piper.LastVoice);
    }

    [Fact]
    public async Task TtsEngine_configuré_est_respecté_même_si_voix_inconnue()
    {
        var store = new StubStore { Settings = new VoiceSettings { TtsEngine = "piper" } };
        var edge = new FakeEngine("EdgeTTS", "fr-FR-HenriNeural");
        var piper = new FakeEngine("Piper");
        var auto = Create(store, edge, piper);

        await auto.SynthesizeWavAsync("bonjour", "");

        Assert.Equal(1, piper.Calls);
        Assert.Equal(0, edge.Calls);
    }

    [Fact]
    public async Task Bascule_sur_le_suivant_quand_le_moteur_échoue()
    {
        var edge = new FakeEngine("EdgeTTS", "fr-FR-HenriNeural") { Fail = true };
        var piper = new FakeEngine("Piper", "fr_FR-upmc-medium");
        var auto = Create(new StubStore(), edge, piper);

        var wav = await auto.SynthesizeWavAsync("bonjour", "fr-FR-HenriNeural");

        Assert.NotEmpty(wav);
        Assert.Equal(1, edge.Calls);
        Assert.Equal(1, piper.Calls);
    }

    [Fact]
    public async Task Falls_back_to_windows_when_all_others_offline()
    {
        // Edge et Piper « hors ligne » : listes de voix vides.
        var edgeOffline = new FakeEngine("EdgeTTS");
        var piperOffline = new FakeEngine("Piper");
        var windows = new FakeEngine("Voix Windows", "Microsoft Hortense Desktop");
        var auto = Create(new StubStore(), edgeOffline, piperOffline, windows);

        await auto.SynthesizeWavAsync("bonjour", "");

        Assert.Equal(1, windows.Calls);
        Assert.Equal(0, edgeOffline.Calls);
        Assert.Equal(0, piperOffline.Calls);
    }

    [Fact]
    public void AvailableVoices_est_l_union_des_moteurs()
    {
        var edge = new FakeEngine("EdgeTTS", "fr-FR-HenriNeural");
        var piper = new FakeEngine("Piper", "fr_FR-upmc-medium");
        var auto = Create(new StubStore(), edge, piper);

        Assert.Equal(2, auto.AvailableVoices.Count);
        Assert.Contains("fr-FR-HenriNeural", auto.AvailableVoices);
        Assert.Contains("fr_FR-upmc-medium", auto.AvailableVoices);
    }
}