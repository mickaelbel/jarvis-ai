using System.Diagnostics;
using System.Text.Json;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Voice;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

// Hub de contenu avancé (docs/hub.py spec) : télécharge une vidéo/audio via yt-dlp,
// la transcrit avec faster-whisper (venv VOIX existant, GPU si dispo) et indexe tout
// dans un coffre markdown (%LOCALAPPDATA%\JarvisAI\vault). YouTube = cas particulier.
public sealed class HubInspirationTool : ITool
{
    private readonly ILogger<HubInspirationTool> _logger;

    public HubInspirationTool(ILogger<HubInspirationTool> logger) => _logger = logger;

    public string Name => "hub";
    public string Description =>
        "Hub d'inspiration : télécharge une vidéo/audio (YouTube et +2000 sites via yt-dlp), la transcrit " +
        "(Whisper local) et indexe le texte dans ton coffre de notes. Actions : " +
        "ingest (télécharge+transcrit une URL), chercher (recherche plein texte dans le coffre), liste.";
    public string Category => "contenu";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "Je télécharge et je transcris… ça peut prendre quelques minutes.";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "ingest | chercher | liste", typeof(string), required: true),
        new("url", "URL de la vidéo/audio (pour ingest)", typeof(string)),
        new("sujet", "mots-clés de recherche (pour chercher)", typeof(string))
    };

    private static string VaultRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "vault");
        Directory.CreateDirectory(Path.Combine(root, "raw"));
        return root;
    }

    private static string? FindYtDlp()
    {
        foreach (var name in new[] { "yt-dlp.exe", "yt-dlp" })
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* entrée PATH invalide */ }
            }
        }

        // winget (install utilisateur) : le dossier Packages n'est pas toujours dans le PATH
        try
        {
            var packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                var hit = Directory.GetFiles(packages, "yt-dlp.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        catch { /* pas grave : message d'erreur explicite à l'appel */ }

        return null;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "ingest" => IngestAsync(parameters.GetValueOrDefault("url") ?? "", ct),
                "chercher" => Task.FromResult(Chercher(parameters.GetValueOrDefault("sujet") ?? "")),
                "liste" => Task.FromResult(Liste()),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : ingest, chercher, liste"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hub] {Action} échoué", action);
            return ToolResult.Failed($"Erreur hub : {ex.Message}");
        }
    }

    private async Task<ToolResult> IngestAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return ToolResult.Failed("Donne-moi l'URL de la vidéo à archiver.");

        var ytdlp = FindYtDlp();
        if (ytdlp is null)
            return ToolResult.Failed("yt-dlp introuvable : installe-le une fois avec « winget install yt-dlp » puis réessaie.");

        var vault = VaultRoot();
        var rawDir = Path.Combine(vault, "raw");

        // 1) Téléchargement audio + métadonnées
        using (var dl = new Process())
        {
            dl.StartInfo = new ProcessStartInfo
            {
                FileName = ytdlp,
                Arguments = $"-x --audio-format mp3 --write-info-json --no-playlist -o \"{Path.Combine(rawDir, "%(id)s.%(ext)s")}\" \"{url}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                RedirectStandardOutput = true
            };
            dl.Start();
            var errTask = dl.StandardError.ReadToEndAsync(ct);
            await dl.WaitForExitAsync(ct);
            if (dl.ExitCode != 0)
            {
                var err = await errTask;
                return ToolResult.Failed($"Téléchargement impossible : {err.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim()}");
            }
        }

        // 2) Métadonnées depuis le json écrit par yt-dlp
        var nouveaux = Directory.GetFiles(rawDir, "*.info.json")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
        if (nouveaux is null)
            return ToolResult.Failed("Téléchargement OK mais métadonnées absentes.");

        var jsonText = await File.ReadAllTextAsync(nouveaux.FullName, ct);
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N")[..12];
        var titre = root.TryGetProperty("title", out var t) ? t.GetString() ?? id : id;
        var chaine = root.TryGetProperty("uploader", out var u) ? u.GetString() ?? "" : "";
        var dureeSec = root.TryGetProperty("duration", out var d) && d.TryGetDouble(out var dd) ? (int)dd : 0;
        var plateforme = root.TryGetProperty("extractor_key", out var ek) ? ek.GetString() ?? "Web" : "Web";

        var mp3 = Path.Combine(rawDir, id + ".mp3");
        if (!File.Exists(mp3))
        {
            var altAudio = Directory.GetFiles(rawDir, id + ".*").FirstOrDefault(f => !f.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase));
            if (altAudio is null) return ToolResult.Failed("Fichier audio absent après téléchargement.");
            mp3 = altAudio;
        }

        // 3) Transcription via le venv VOIX (faster-whisper déjà installé, GPU auto)
        var python = VoicePaths.FindPythonVenv();
        if (python is null)
            return ToolResult.Failed("Venv voix introuvable — la transcription nécessite l'installation voix (déjà en place normalement).");

        var transcription = await TranscrireAsync(python, mp3, ct);

        // 4) Fiche markdown + index
        var fiche = Path.Combine(vault, id + ".md");
        var dureeTxt = TimeSpan.FromSeconds(dureeSec).ToString(@"hh\:mm\:ss");
        await File.WriteAllTextAsync(fiche,
            $"---\ntitre: {titre}\nsource: {url}\nplateforme: {plateforme}\nchaine: {chaine}\nduree: {dureeTxt}\narchi_le: {DateTime.Now:yyyy-MM-dd HH:mm}\n---\n\n# {titre}\n\n{transcription}\n", ct);

        var index = Path.Combine(vault, "index.md");
        var indexLines = File.Exists(index) ? await File.ReadAllLinesAsync(index, ct) : Array.Empty<string>();
        if (!indexLines.Any(l => l.Contains(id)))
            await File.AppendAllTextAsync(index, $"- [{titre}]({id}.md) — {plateforme} · {chaine} · {dureeTxt} · {DateTime.Now:dd/MM/yyyy}\n", ct);

        var mots = transcription.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        _logger.LogInformation("[Hub] Ingesté : {Titre} ({Mots} mots)", titre, mots);
        return ToolResult.Succeeded(
            $"« {titre} » archivé ({plateforme}, {chaine}, {dureeTxt}) et transcrit : {mots} mots indexés dans le coffre. ACTION TERMINÉE.");
    }

    private static async Task<string> TranscrireAsync(string python, string audioPath, CancellationToken ct)
    {
        const string script =
            """
            import sys
            from faster_whisper import WhisperModel
            model = WhisperModel(sys.argv[2], device="auto", compute_type="auto")
            segments, _ = model.transcribe(sys.argv[1], language="fr", beam_size=1, vad_filter=True)
            print("\n".join(s.text.strip() for s in segments))
            """;

        // « python - <args> » lit le programme sur stdin et expose les args dans sys.argv
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"- \"{audioPath}\" small",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        proc.Start();
        await proc.StandardInput.WriteAsync(script.AsMemory(), ct);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException($"Transcription échouée : {stderr.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim()}");
        }
        return stdout.Trim();
    }

    private static ToolResult Chercher(string sujet)
    {
        if (string.IsNullOrWhiteSpace(sujet))
            return ToolResult.Failed("Donne-moi des mots-clés à chercher dans le coffre.");

        // Recherche pondérée TF-IDF sur stems (accents/pluriels ignorés) :
        // « vidéos productivité » retrouve « vidéo sur la productivité ».
        var queryTokens = Tokenize(sujet);
        if (queryTokens.Count == 0)
            return ToolResult.Failed("Mots-clés non exploitables — précise ta recherche.");

        var vault = VaultRoot();
        var scored = new List<(string Titre, string Extrait, double Score)>();
        var totalDocs = 0;

        foreach (var md in Directory.GetFiles(vault, "*.md").Where(f => !f.EndsWith("index.md", StringComparison.OrdinalIgnoreCase)))
        {
            totalDocs++;
            var lines = File.ReadAllLines(md);
            var titre = lines.FirstOrDefault(l => l.StartsWith("# "))?[2..] ?? Path.GetFileNameWithoutExtension(md);

            double bestScore = 0;
            string? bestLine = null;
            var docFreq = new Dictionary<string, int>();

            foreach (var line in lines)
            {
                var toks = Tokenize(line);
                if (toks.Count == 0) continue;
                foreach (var t in toks)
                    docFreq[t] = docFreq.GetValueOrDefault(t) + 1;

                var score = 0.0;
                foreach (var q in queryTokens)
                {
                    var tf = docFreq.GetValueOrDefault(q);
                    if (tf > 0)
                        score += 1 + Math.Log(tf); // fréquence du terme dans la ligne
                }
                // Bonus si la ligne contient TOUTES les requêtes
                if (queryTokens.All(q => docFreq.ContainsKey(q))) score *= 1.5;
                if (score > bestScore) { bestScore = score; bestLine = line.Trim(); }
            }

            if (bestScore > 0 && bestLine is not null)
            {
                // IDF global approximé : rare dans le coffre = plus discriminant
                var coverage = (double)queryTokens.Count(q => docFreq.ContainsKey(q)) / queryTokens.Count;
                scored.Add((titre, bestLine, bestScore * (0.5 + coverage)));
            }
            docFreq.Clear();
        }

        if (scored.Count == 0)
            return ToolResult.Succeeded($"Rien sur « {sujet} » dans le coffre pour l'instant ({totalDocs} documents indexés).");

        var top = scored.OrderByDescending(s => s.Score).Take(8);
        return ToolResult.Succeeded($"{scored.Count} correspondance(s) pour « {sujet} » :\n" +
            string.Join("\n", top.Select((s, i) => $"  {i + 1}. {s.Titre} (pertinence {s.Score:F1}) : {Truncate(s.Extrait, 200)}")));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "…";

    /// <summary>Tokenisation tolérante : minuscules, sans accents, stems légers FR/EN.</summary>
    private static List<string> Tokenize(string text)
    {
        var results = new List<string>();
        foreach (var raw in text.Split([' ', '\t', '\n', '\r', ',', ';', ':', '.', '!', '?', '(', ')', '"', '\'', '-', '/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(t.Length);
            foreach (var c in t)
            {
                var cat = char.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }
            foreach (var word in sb.ToString().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length < 3 || StopWords.Contains(word)) continue;
                results.Add(Stem(word));
            }
        }
        return results;
    }

    private static string Stem(string w)
    {
        // Stems légers suffisant pour de la recherche documentaire FR
        string[] suffixes = ["ements", "ement", "ations", "ation", "eurs", "euses", "euse",
            "eurs", "ités", "ité", "ives", "ive", "ifs", "if", "aux", "ales", "ale",
            "ment", "tion", "sses", "sse", "ches", "che", "ers", "er", "ées", "ée",
            "és", "es", "s", "x"];
        foreach (var suf in suffixes)
        {
            if (w.Length - suf.Length >= 4 && w.EndsWith(suf, StringComparison.Ordinal))
                return w[..^suf.Length];
        }
        return w;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "les","des","une","que","qui","quoi","dans","pour","par","sur","avec","sans","chez",
        "est","sont","était","plus","tout","tous","toute","cette","ces","son","sa","ses",
        "leur","leurs","mais","donc","alors","comme","aussi","fait","faire","the","and",
        "for","with","from","that","this","have","are","was","were","sur","comment","pourquoi"
    };

    private static ToolResult Liste()
    {
        var index = Path.Combine(VaultRoot(), "index.md");
        if (!File.Exists(index)) return ToolResult.Succeeded("Le coffre est vide — donne-moi une URL avec hub ingest pour commencer.");
        var entries = File.ReadAllLines(index).Where(l => l.StartsWith('-')).TakeLast(15);
        return ToolResult.Succeeded("CONTENU ARCHIVÉ :\n" + string.Join("\n", entries.Select(l => $"  {l}")));
    }
}