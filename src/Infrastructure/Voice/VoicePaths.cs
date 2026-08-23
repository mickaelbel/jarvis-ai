namespace JarvisAI.Infrastructure.Voice;

public static class VoicePaths
{
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
