using JarvisAI.Application.AI;
using JarvisAI.Application.AutoImprovement;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Dev;

/// <summary>Abstraction du redémarrage de l'application (implémentée par l'hôte).</summary>
public interface ISelfDevLifecycle
{
    void Restart();
}

// ── Réglages (selfdev.json) ───────────────────────────────────────────────
public sealed class SelfDevSettings
{
    /// <summary>Boucle de vérification périodique activée.</summary>
    public bool AutoCheckEnabled { get; set; } = true;
    /// <summary>Intervalle entre deux vérifications (minutes).</summary>
    public int IntervalMinutes { get; set; } = 720;
    /// <summary>Tenter une correction par IA quand build/tests sont rouges.</summary>
    public bool AutoFixWithAi { get; set; } = true;
    /// <summary>Publier automatiquement après une vérification verte.</summary>
    public bool AutoPublishOnGreen { get; set; } = false;
    /// <summary>Plafond de tentatives d'auto-fix par jour (sécurité).</summary>
    public int MaxAutoFixPerDay { get; set; } = 3;
}

/// <summary>Magasin JSON atomique des réglages d'auto-développement.</summary>
public sealed class SelfDevStore
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "selfdev.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();
    private SelfDevSettings _settings;

    public SelfDevStore() => _settings = Load();

    public SelfDevSettings Get() { lock (_lock) return Clone(_settings); }

    public void Save(SelfDevSettings s)
    {
        lock (_lock)
        {
            _settings = Clone(s);
            Security.SafeFileWriter.WriteText(Path_, JsonSerializer.Serialize(s, JsonOpts));
        }
    }

    private static SelfDevSettings Clone(SelfDevSettings s) =>
        JsonSerializer.Deserialize<SelfDevSettings>(JsonSerializer.Serialize(s)) ?? new();

    private static SelfDevSettings Load()
    {
        if (!File.Exists(Path_)) return new SelfDevSettings();
        try
        {
            return JsonSerializer.Deserialize<SelfDevSettings>(File.ReadAllText(Path_), JsonOpts) ?? new();
        }
        catch { return new SelfDevSettings(); }
    }
}

// ── Moteur ────────────────────────────────────────────────────────────────
/// <summary>
/// Permet à Jarvis de développer sur son propre code : build, tests, points de
/// restauration git, publication, redémarrage, logs — et auto-réparation par IA
/// sous garde-fous (checkpoint avant, rollback si les tests restent rouges).
/// </summary>
public sealed class SelfDevEngine
{
    private static readonly string[] AllowedFixPrefixes = ["src/", "Tests/", "Plugins/", "scripts/"];

    private readonly Lazy<AIService> _ai;
    private readonly ISelfImprovementManager? _lessons;
    private readonly ISelfDevLifecycle? _lifecycle;
    private readonly ILogger<SelfDevEngine> _logger;
    private readonly object _fixLock = new();

    public SelfDevEngine(
        Lazy<AIService> ai,
        ILogger<SelfDevEngine> logger,
        ISelfImprovementManager? lessons = null,
        ISelfDevLifecycle? lifecycle = null)
    {
        _ai = ai;
        _lessons = lessons;
        _lifecycle = lifecycle;
        _logger = logger;
    }

    private void Note(string lesson) => _lessons?.AddLesson(lesson);

    /// <summary>Racine du dépôt : répertoire contenant JarvisAI.sln, remontée depuis l'exécutable.</summary>
    public string? FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "JarvisAI.sln")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        return null;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string repo, string fileName, string arguments, int timeoutMinutes)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var finished = await Task.Run(() => proc.WaitForExit(1000 * 60 * timeoutMinutes));
        if (!finished)
        {
            try { proc.Kill(true); } catch { }
            return (-1, "TIMEOUT après " + timeoutMinutes + " min");
        }
        var output = await stdoutTask + "\n" + await stderrTask;
        return (proc.ExitCode, output);
    }

    public async Task<DotnetResult> BuildAsync(int timeoutMinutes = 8)
    {
        var repo = FindRepo();
        if (repo is null) return new DotnetResult { Success = false, Errors = ["JarvisAI.sln introuvable"] };
        var (code, output) = await RunAsync(repo, "dotnet", $"build \"{System.IO.Path.Combine(repo, "JarvisAI.sln")}\" -c Release --nologo -v q", timeoutMinutes);
        var result = DotnetOutputParser.ParseBuild(output);
        if (code != 0 && result.Errors.Count == 0)
        {
            var trimmed = output.Trim();
            result = result with { Success = false, Errors = [trimmed[..Math.Min(800, trimmed.Length)]] };
        }
        else if (code != 0) result = result with { Success = false };
        return result;
    }

    public async Task<DotnetResult> TestAsync(string? filter = null, int timeoutMinutes = 15)
    {
        var repo = FindRepo();
        if (repo is null) return new DotnetResult { Success = false, Errors = ["JarvisAI.sln introuvable"] };
        var filterArg = string.IsNullOrWhiteSpace(filter) ? "" : $" --filter \"{filter}\"";
        var (code, output) = await RunAsync(repo, "dotnet", $"test \"{System.IO.Path.Combine(repo, "JarvisAI.sln")}\" -c Release --nologo{filterArg}", timeoutMinutes);
        var parsed = DotnetOutputParser.ParseTest(output);
        if (!parsed.Success && code != 0 && parsed.Failures.Count == 0)
        {
            var trimmed = output.Trim();
            parsed = parsed with { Errors = new[] { trimmed[..Math.Min(600, trimmed.Length)] }.Concat(parsed.Errors).ToList() };
        }
        return parsed;
    }

    // ── Git : point de restauration / rollback ────────────────────────────
    public async Task<(bool Ok, string Info)> CheckpointAsync(string message)
    {
        var repo = FindRepo();
        if (repo is null) return (false, "dépôt introuvable");
        await EnsureGitIgnore(repo);

        async Task<string> Git(string args)
        {
            var (_, outp) = await RunAsync(repo, "git", args, 2);
            return outp;
        }

        await Git("init");
        await Git("add -A");
        var status = await Git("status --porcelain");
        if (string.IsNullOrWhiteSpace(status))
            return (true, "rien de nouveau à committer (dépôt déjà propre)");

        var commit = await Git($"commit -m \"Jarvis: {message.Replace("\"", "'")}\"");
        var t = commit.Trim();
        if (t.Contains("files changed") || t.Contains("fichier", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("master", StringComparison.OrdinalIgnoreCase) || t.Contains("main", StringComparison.OrdinalIgnoreCase))
            return (true, "checkpoint créé");
        return (false, t[..Math.Min(300, t.Length)]);
    }

    /// <summary>Annule toutes les modifications non committées (rollback).</summary>
    public async Task<(bool Ok, string Info)> RollbackAsync()
    {
        var repo = FindRepo();
        if (repo is null) return (false, "dépôt introuvable");
        var (code, _) = await RunAsync(repo, "git", "checkout -- .", 2);
        await RunAsync(repo, "git", "clean -fd src Tests Plugins scripts", 2);
        _logger.LogInformation("[SelfDev] Rollback effectué (checkout+clean)");
        return (code == 0, code == 0 ? "modifications annulées, retour au dernier checkpoint" : "git checkout a échoué");
    }

    public async Task<string> StatusAsync()
    {
        var repo = FindRepo();
        if (repo is null) return "Dépôt introuvable (JarvisAI.sln absent).";
        var (_, outp) = await RunAsync(repo, "git", "status --porcelain -b", 2);
        var dirty = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return dirty.Length == 0
            ? "Dépôt propre — tout est committé."
            : $"{dirty.Length} fichier(s) modifié(s) non committé(s).";
    }

    private static async Task EnsureGitIgnore(string repo)
    {
        var path = System.IO.Path.Combine(repo, ".gitignore");
        if (File.Exists(path)) return;
        var content = """
            bin/
            obj/
            dist/
            .vs/
            *.user
            *.log
            **/.venv*/
            __pycache__/
            voice/models/
            **/piper/**/espeak-ng-data/
            *.onnx
            *.onnx.json
            captures/
            Tests/TestResults/
            hub/coffre/
            """;
        Security.SafeFileWriter.WriteText(path, content);
        await Task.CompletedTask;
    }

    // ── Publication / installation / redémarrage ──────────────────────────
    public async Task<(bool Ok, string Info)> PublishAsync(bool skipDependencies = true)
    {
        var repo = FindRepo();
        if (repo is null) return (false, "dépôt introuvable");
        var script = System.IO.Path.Combine(repo, "publish.ps1");
        if (!File.Exists(script)) return (false, "publish.ps1 introuvable");
        var flags = skipDependencies ? " -SkipDependencies" : "";
        var (code, output) = await RunAsync(repo, "powershell",
            $"-ExecutionPolicy Bypass -File \"{script}\"{flags}", 15);
        return (code == 0, code == 0 ? "publication réussie (dist\\JarvisAI)" : Tail(output, 500));
    }

    public async Task<(bool Ok, string Info)> BuildInstallerAsync()
    {
        var repo = FindRepo();
        if (repo is null) return (false, "dépôt introuvable");
        var script = System.IO.Path.Combine(repo, "scripts", "build-installer.ps1");
        if (!File.Exists(script)) return (false, "scripts/build-installer.ps1 introuvable");
        var (code, output) = await RunAsync(repo, "powershell",
            $"-ExecutionPolicy Bypass -File \"{script}\" -SkipDependencies", 20);
        return (code == 0, code == 0 ? "installateur généré" : Tail(output, 500));
    }

    public (bool Ok, string Info) RestartApp()
    {
        if (_lifecycle is null) return (false, "pas de contrôleur de cycle de vie dans ce contexte");
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            try { _lifecycle.Restart(); } catch (Exception ex) { _logger.LogError(ex, "[SelfDev] échec du redémarrage"); }
        });
        return (true, "redémarrage lancé");
    }

    public string TailLog(string which, int lines)
    {
        var file = which.ToLowerInvariant() switch
        {
            "web" => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI", "web.log"),
            "desktop" => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI", "desktop.log"),
            _ => ""
        };
        if (file == "" || !File.Exists(file)) return $"Journal « {which} » introuvable.";
        // FileShare.ReadWrite : le journal peut être ouvert par l'application en même temps.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var all = new List<string>();
        while (reader.ReadLine() is { } line) all.Add(line);
        return string.Join("\n", all.TakeLast(Math.Clamp(lines, 1, 400)));
    }

    // ── Auto-réparation par IA (garde-fous stricts) ───────────────────────
    /// <summary>
    /// Cycle complet : diagnostic → checkpoint → patch IA limité au projet →
    /// rebuild+retest. Vert : commit. Rouge : rollback intégral.
    /// </summary>
    public async Task<string> TryAutoFixAsync(DotnetResult? knownFailure = null)
    {
        lock (_fixLock)
        {
            if (_fixRunning) return "Un auto-fix est déjà en cours.";
            _fixRunning = true;
        }
        try
        {
            var store = new SelfDevStore();
            var settings = store.Get();
            if (settings.AutoFixWithAi == false && knownFailure is null)
                return "Auto-fix désactivé (« autodev boucle config autofix on » ou « autodev fix » pour forcer).";

            var today = DateTime.Today;
            if (_lastFixDay != today) { _lastFixDay = today; _fixesToday = 0; }
            if (_fixesToday >= settings.MaxAutoFixPerDay)
                return $"Plafond d'auto-fix atteint ({settings.MaxAutoFixPerDay}/jour).";

            _logger.LogInformation("[SelfDev] Auto-fix démarré");
            await CheckpointAsync("avant auto-fix automatique");

            var failure = knownFailure ?? await TestAsync();
            if (failure.Success)
            {
                var build = await BuildAsync();
                if (build.Success) return "Rien à corriger : build et tests déjà verts.";
                failure = build;
            }

            var diagnostic = Describe(failure);
            _logger.LogInformation("[SelfDev] Diagnostic envoyé à l'IA : {Diag}", diagnostic[..Math.Min(400, diagnostic.Length)]);

            var prompt = $$"""
                Tu es Jarvis et tu répares ton propre code C# (.NET 8, WPF + Blazor).
                Voici la sortie de dotnet build/test qui échoue :
                ```
                {{diagnostic}}
                ```
                Réponds UNIQUEMENT avec un objet JSON (aucun texte autour) :
                {"explanation":"une phrase","changes":[{"file":"chemin/relatif/depuis/la/racine.cs","content":"CONTENU COMPLET du fichier corrigé"}]}
                Contraintes impératives :
                - Seuls les fichiers sous src/, Tests/, Plugins/, scripts/ peuvent être touchés (max 5 fichiers).
                - content doit être le fichier ENTIER, prêt à écrire tel quel.
                - Ne change JAMAIS le comportement prévu par les tests existants : fais passer les tests en corrigeant le bug réel.
                - Si tu ne sais pas corriger avec certitude, renvoie {"explanation":"incertain","changes":[]}.
                """;

            AIResponse resp;
            try
            {
                resp = await _ai.Value.ChatAsync(prompt, new AIConversation(
                    "Tu es un ingénieur logiciel senior qui répare un projet C#. Réponses en JSON pur."));
            }
            catch (Exception ex)
            {
                Note("[SelfDev] auto-fix impossible : IA injoignable (" + ex.Message + ")");
                return "IA injoignable pour l'auto-fix : " + ex.Message;
            }

            var (applied, files) = ExtractAndApplyPatch(resp.Content, FindRepo());
            if (!applied)
            {
                Note("[SelfDev] auto-fix abandonné : pas de patch exploitable dans la réponse IA.");
                return "L'IA n'a pas proposé de patch exploitable — rien modifié.";
            }

            // Vérification stricte
            var rebuild = await BuildAsync();
            if (!rebuild.Success)
            {
                await RollbackAsync();
                Note("[SelfDev] auto-fix annulé (build rouge après patch) : " + Describe(rebuild)[..200]);
                return "Patch appliqué mais build toujours rouge → rollback effectué.";
            }
            var retest = await TestAsync();
            if (!retest.Success)
            {
                await RollbackAsync();
                Note("[SelfDev] auto-fix annulé (tests rouges après patch) : " + Describe(retest)[..200]);
                return $"Patch appliqué mais {retest.Failed} test(s) encore rouge(s) → rollback effectué.";
            }

            _fixesToday++;
            await CheckpointAsync("auto-fix IA : " + files.Count + " fichier(s)");
            Note("[SelfDev] auto-fix réussi (" + string.Join(", ", files) + ")");
            return "Auto-réparation réussie ! Fichiers modifiés : " + string.Join(", ", files) + ". Build + tests verts, checkpoint créé.";
        }
        finally
        {
            lock (_fixLock) _fixRunning = false;
        }
    }
    private bool _fixRunning;
    private DateTime _lastFixDay = DateTime.MinValue;
    private int _fixesToday;

    private (bool Applied, List<string> Files) ExtractAndApplyPatch(string content, string? repo)
    {
        var files = new List<string>();
        if (repo is null || string.IsNullOrWhiteSpace(content)) return (false, files);

        var jsonStart = content.IndexOf('{');
        var jsonEnd = content.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart) return (false, files);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(content[jsonStart..(jsonEnd + 1)]); }
        catch { return (false, files); }

        if (!doc.RootElement.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            return (false, files);

        if (changes.GetArrayLength() == 0 || changes.GetArrayLength() > 5) return (false, files);

        foreach (var change in changes.EnumerateArray())
        {
            if (!change.TryGetProperty("file", out var fileEl) || !change.TryGetProperty("content", out var contentEl))
                continue;
            var rel = fileEl.GetString()?.Replace('\\', '/') ?? "";
            if (rel.Contains("..") || !AllowedFixPrefixes.Any(p => rel.StartsWith(p, StringComparison.Ordinal)))
                continue; // chemin hors périmètre → rejet silencieux

            var abs = System.IO.Path.Combine(repo, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, contentEl.GetString() ?? "");
            files.Add(rel);
        }
        return (files.Count > 0, files);
    }

    private static string Describe(DotnetResult r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(r.Passed > 0 || r.Failed > 0 ? $"Tests : {r.Passed} passés, {r.Failed} échoués." : "Build en échec.");
        foreach (var e in r.Errors.Take(6)) sb.AppendLine(e);
        foreach (var f in r.Failures.Take(10))
        {
            sb.AppendLine($"ÉCHEC {f.Name}");
            if (!string.IsNullOrWhiteSpace(f.Message)) sb.AppendLine("  " + f.Message[..Math.Min(300, f.Message.Length)]);
        }
        return sb.ToString();
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : s[^max..];
}

// ── Boucle périodique ─────────────────────────────────────────────────────
/// <summary>Vérifie périodiquement build+tests ; déclenche l'auto-fix si rouge.</summary>
public sealed class SelfDevLoop : BackgroundService
{
    private readonly SelfDevEngine _engine;
    private readonly SelfDevStore _store;
    private readonly ILogger<SelfDevLoop> _logger;

    public SelfDevLoop(SelfDevEngine engine, SelfDevStore store, ILogger<SelfDevLoop> logger)
    {
        _engine = engine;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Première vérification 10 min après le démarrage (laisser l'app se stabiliser)
        try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _store.Get();
            if (!settings.AutoCheckEnabled)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                _logger.LogInformation("[SelfDev] Vérification périodique…");
                var test = await _engine.TestAsync();
                if (test.Success)
                {
                    _logger.LogInformation("[SelfDev] Vérification verte ({Passed} tests)", test.Passed);
                    if (settings.AutoPublishOnGreen)
                    {
                        var (ok, info) = await _engine.PublishAsync();
                        _logger.LogInformation("[SelfDev] Publication auto : {Info}", info);
                    }
                }
                else
                {
                    _logger.LogWarning("[SelfDev] Vérification ROUGE ({Failed} échecs) → auto-fix", test.Failed);
                    var verdict = await _engine.TryAutoFixAsync(test);
                    _logger.LogInformation("[SelfDev] Verdict auto-fix : {Verdict}", verdict);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SelfDev] erreur de la boucle de maintenance");
            }

            var interval = TimeSpan.FromMinutes(Math.Clamp(settings.IntervalMinutes, 30, 24 * 60));
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}