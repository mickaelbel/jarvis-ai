namespace JarvisAI.Infrastructure.Voice;

public static class VoicePaths
{
    // ── Ports des serveurs Python (centrale unique — pas de hardcode ailleurs) ──
    public const int SttPort = 17001;
    public const int WakeWordPort = 17002;
    public const int XttsPort = 17003;
    public const int EdgeTtsPort = 17004;
    // Serveur Edge TTS de secours : si le principal (17004) est injoignable, la
    // synthèse bascule automatiquement sur ce port (failover réseau, item [102]).
    public const int EdgeTtsPort2 = 17005;

    private const string Host = "127.0.0.1";

    public static string SttBase => $"http://{Host}:{SttPort}";
    public static string WakeWordBase => $"http://{Host}:{WakeWordPort}";
    public static string XttsBase => $"http://{Host}:{XttsPort}";
    public static string EdgeTtsBase => $"http://{Host}:{EdgeTtsPort}";
    public static string EdgeTtsBase2 => $"http://{Host}:{EdgeTtsPort2}";

    public static string SttHealth => $"{SttBase}/health";
    public static string WakeWordHealth => $"{WakeWordBase}/health";
    public static string XttsHealth => $"{XttsBase}/health";
    public static string EdgeTtsHealth => $"{EdgeTtsBase}/health";
    public static string EdgeTtsHealth2 => $"{EdgeTtsBase2}/health";
    public static string? FindVoiceDirectory()
    {
        var start = AppContext.BaseDirectory;
        var current = new DirectoryInfo(start);

        for (var i = 0; i < 8 && current is not null; i++)
        {
            var candidate = Path.Combine(current.FullName, "voice");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "stt_server.py")))
                return candidate;

            var jwCandidate = Path.Combine(current.FullName, "src", "Web", "JarvisAI.Web", "voice");
            if (Directory.Exists(jwCandidate) && File.Exists(Path.Combine(jwCandidate, "stt_server.py")))
                return jwCandidate;

            current = current.Parent;
        }

        return null;
    }

    public static string? FindPythonVenv(VoicePathsResult? result = null)
    {
        var voiceDir = result?.VoiceDirectory ?? FindVoiceDirectory();
        if (voiceDir is null) return null;

        var exe = Path.Combine(voiceDir, ".venv", "Scripts", "python.exe");
        return File.Exists(exe) ? exe : null;
    }

    public static string? FindSttServerScript()
    {
        var voiceDir = FindVoiceDirectory();
        if (voiceDir is null) return null;
        var script = Path.Combine(voiceDir, "stt_server.py");
        return File.Exists(script) ? script : null;
    }

    public static string? FindWakeWordServerScript()
    {
        var voiceDir = FindVoiceDirectory();
        if (voiceDir is null) return null;
        var script = Path.Combine(voiceDir, "wakeword_server.py");
        return File.Exists(script) ? script : null;
    }

    public static string? FindPiperDirectory()
    {
        var voiceDir = FindVoiceDirectory();
        if (voiceDir is null) return null;

        var candidates = new[]
        {
            Path.Combine(voiceDir, "piper", "piper"),
            Path.Combine(voiceDir, "piper")
        };

        foreach (var candidate in candidates)
        {
            var exe = Path.Combine(candidate, "piper.exe");
            if (File.Exists(exe)) return candidate;
        }

        return null;
    }
}

public sealed record VoicePathsResult(string VoiceDirectory);
