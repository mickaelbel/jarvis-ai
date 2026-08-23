using System.Text.Json;
using JarvisAI.Web.Services;

namespace JarvisAI.Tests;

public class AppVoiceStatusTests
{
    [Fact]
    public void Default_states_are_unknown_and_inactive()
    {
        var status = new AppVoiceStatus();
        var snap = status.Snapshot();

        Assert.False(snap.EngineActive);
        Assert.Equal("idle", snap.EngineState);
        Assert.Equal("inconnu", snap.CaptureMode);
        Assert.Equal("inconnu", snap.MicroState);
        Assert.Equal("inconnu", snap.WakeWordState);
        Assert.Equal("inconnu", snap.SttState);
        Assert.Equal("inconnu", snap.TtsState);
        Assert.Equal("inconnu", snap.ListeningState);
        Assert.Null(snap.StartedAt);
    }

    [Fact]
    public void Status_updates_are_visible_in_snapshot()
    {
        var status = new AppVoiceStatus();
        status.EngineActive = true;
        status.EngineState = "listening";
        status.CaptureMode = "WASAPI";
        status.MicroState = "actif";
        status.WakeWordState = "continu (sans mot-clé)";
        status.SttState = "prêt";
        status.TtsState = "prêt";
        status.ListeningState = "écoute active";
        status.StartedAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

        var snap = status.Snapshot();

        Assert.True(snap.EngineActive);
        Assert.Equal("listening", snap.EngineState);
        Assert.Equal("WASAPI", snap.CaptureMode);
        Assert.Equal("actif", snap.MicroState);
        Assert.Equal("continu (sans mot-clé)", snap.WakeWordState);
        Assert.Equal("prêt", snap.SttState);
        Assert.Equal("prêt", snap.TtsState);
        Assert.Equal("écoute active", snap.ListeningState);
        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), snap.StartedAt);
    }

    [Fact]
    public void Snapshot_serializes_with_camelcase_ui_keys()
    {
        var status = new AppVoiceStatus();
        status.EngineActive = true;
        status.EngineState = "listening";
        status.CaptureMode = "WASAPI";
        status.MicroState = "actif";
        status.WakeWordState = "continu (sans mot-clé)";
        status.SttState = "prêt";
        status.TtsState = "lecture";
        status.ListeningState = "occupé";
        status.StartedAt = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(status.Snapshot(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var key in new[]
        {
            "engineActive", "engineState", "engineMessage", "captureMode",
            "microState", "wakeWordState", "sttState", "ttsState",
            "listeningState", "startedAt"
        })
        {
            Assert.True(root.TryGetProperty(key, out _), $"La clé camelCase '{key}' doit être présente dans le payload du panneau vocal.");
        }
    }

    [Fact]
    public void Desktop_pipeline_transitions_are_reflected()
    {
        var status = new AppVoiceStatus();

        // Capture WASAPI démarre.
        status.EngineActive = true;
        status.EngineState = "listening";
        status.CaptureMode = "WASAPI";
        status.MicroState = "actif";
        status.ListeningState = "écoute active";

        // Une phrase est détectée → STT en cours.
        status.EngineState = "processing";
        status.SttState = "traitement";
        status.ListeningState = "occupé";

        var processing = status.Snapshot();
        Assert.Equal("traitement", processing.SttState);
        Assert.Equal("occupé", processing.ListeningState);

        // TTS joue la réponse, puis retour à l'écoute.
        status.TtsState = "lecture";
        var speaking = status.Snapshot();
        Assert.Equal("lecture", speaking.TtsState);

        status.TtsState = "prêt";
        status.EngineState = "listening";
        status.SttState = "prêt";
        status.ListeningState = "écoute active";

        var back = status.Snapshot();
        Assert.Equal("prêt", back.SttState);
        Assert.Equal("prêt", back.TtsState);
        Assert.Equal("écoute active", back.ListeningState);
    }
}
