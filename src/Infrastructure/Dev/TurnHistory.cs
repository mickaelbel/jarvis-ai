using JarvisAI.Application.Voice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Dev;

// ── Primitives git (implémentées par SelfDevEngine, substituables en test) ──
public interface IGitTurnOps
{
    string? FindRepo();
    Task<string?> CommitHeadShaAsync();
    Task<(string? Sha, bool WasDirty)> CommitAllAsync(string message);
    Task<List<string>> DiffNamesFromAsync(string? fromSha);
    /// <summary>Commit parent du SHA donné (null si racine ou introuvable).</summary>
    Task<string?> ParentShaAsync(string sha);
    Task<bool> CheckoutTreeAsync(string sha);
    Task<bool> CheckoutFilesAsync(string sha, IReadOnlyList<string> files);
    Task<bool> FileExistsInAsync(string? sha, string path);
    /// <summary>Supprime un fichier du dépôt (chemin relatif) + répertoires vides.</summary>
    Task<bool> DeleteFileAsync(string path);
}

/// <summary>
/// Un « tour » = une demande utilisateur (chat ou voix) et sa réponse. Chaque tour
/// est committé dans git avec la liste des fichiers touchés, ce qui permet un
/// retour arrière conversationnel : annuler les derniers changements de code
/// (« annule tes derniers changements ») ou restaurer sélectivement.
/// </summary>
public sealed record TurnRecord(
    string Id,
    DateTime AtUtc,
    string Source,
    string UserText,
    string? Response,
    string? GitShaAfter,
    List<string> ChangedFiles);

public interface ITurnHistory
{
    /// <summary>Enregistre un tour : commit du worktree + empreinte des fichiers modifiés.</summary>
    Task<TurnRecord?> RecordAsync(string source, string userText, string? response);
    IReadOnlyList<TurnRecord> List(int max = 30);
    int Count { get; }
    /// <summary>Annule tous les tours à partir de celui donné (inclus). Id court accepté.</summary>
    Task<string> RewindBeforeAsync(string turnId);
    /// <summary>Annule les N derniers tours.</summary>
    Task<string> UndoLastAsync(int count = 1);
    /// <summary>Restaure l'état d'avant le Nième dernier tour en conservant certains chemins.</summary>
    Task<string> RestoreSelectiveAsync(int backCount, IReadOnlyList<string>? keepPaths);
}

// ── Service ───────────────────────────────────────────────────────────────
public sealed class TurnHistoryService : ITurnHistory
{
    private const int MaxTurns = 200;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly IGitTurnOps _dev;
    private readonly ILogger<TurnHistoryService>? _logger;
    private readonly string _path;
    private List<TurnRecord> _turns;

    public TurnHistoryService(IGitTurnOps dev, ILogger<TurnHistoryService>? logger = null, string? filePath = null)
    {
        _dev = dev;
        _logger = logger;
        _path = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "turn-history.json");
        _turns = Load();
    }

    public int Count { get { lock (_lock) return _turns.Count; } }

    public async Task<TurnRecord?> RecordAsync(string source, string userText, string? response)
    {
        if (string.IsNullOrWhiteSpace(userText)) return null;
        var before = await _dev.CommitHeadShaAsync();
        List<string> changed = [];
        try { changed = await _dev.DiffNamesFromAsync(before); }
        catch (Exception ex) { _logger?.LogWarning(ex, "[Historique] diff du tour impossible"); }

        var label = Truncate(userText.ReplaceLineEndings(" "), 60);
        string? shaAfter;
        bool wasDirty;
        try { (shaAfter, wasDirty) = await _dev.CommitAllAsync($"tour {source}: {label}"); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Historique] commit du tour échoué");
            shaAfter = before;
            wasDirty = false;
        }

        var record = new TurnRecord(
            Id: Guid.NewGuid().ToString("N")[..12],
            AtUtc: DateTime.UtcNow,
            Source: source,
            UserText: Truncate(userText, 4000),
            Response: Truncate(response, 2000),
            GitShaAfter: shaAfter ?? before,
            ChangedFiles: wasDirty ? changed : []);

        lock (_lock)
        {
            _turns.Add(record);
            if (_turns.Count > MaxTurns) _turns.RemoveRange(0, _turns.Count - MaxTurns);
            SaveLocked();
        }
        return record;
    }

    public IReadOnlyList<TurnRecord> List(int max = 30)
    {
        lock (_lock) return _turns.TakeLast(Math.Clamp(max, 1, MaxTurns)).ToList();
    }

    public async Task<string> RewindBeforeAsync(string turnId)
    {
        List<TurnRecord> removed;
        string? prevSha;
        lock (_lock)
        {
            var idx = FindIndex(turnId);
            if (idx < 0) return $"Tour « {turnId} » introuvable dans l'historique.";
            prevSha = idx > 0 ? _turns[idx - 1].GitShaAfter : null;
            removed = _turns.Skip(idx).ToList();
        }

        var result = await RevertTurnsAsync(removed, prevSha, keepPaths: null);

        lock (_lock)
        {
            var idx2 = FindIndex(turnId);
            if (idx2 >= 0)
            {
                _turns.RemoveRange(idx2, _turns.Count - idx2);
                SaveLocked();
            }
        }
        return $"Retour avant le tour {turnId} effectué ({result}).";
    }

    public Task<string> UndoLastAsync(int count = 1)
    {
        List<TurnRecord> snapshot;
        lock (_lock) snapshot = [.. _turns];
        if (snapshot.Count == 0) return Task.FromResult("Aucun tour enregistré à annuler.");
        var n = Math.Clamp(count, 1, snapshot.Count);
        var target = snapshot[^n];
        return RewindBeforeAsync(target.Id);
    }

    public async Task<string> RestoreSelectiveAsync(int backCount, IReadOnlyList<string>? keepPaths)
    {
        List<TurnRecord> snapshot;
        lock (_lock) snapshot = [.. _turns];
        if (snapshot.Count == 0) return "Aucun tour enregistré.";

        var n = Math.Clamp(backCount, 1, snapshot.Count);
        var idx = snapshot.Count - n;
        var turn = snapshot[idx];
        var prevSha = idx > 0 ? snapshot[idx - 1].GitShaAfter : null;
        var keep = new HashSet<string>(
            (keepPaths ?? []).Select(NormalizePath).Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        if (turn.ChangedFiles.Count == 0)
            return $"Le tour {turn.Id} n'a modifié aucun fichier — rien à restaurer.";

        var summary = await RevertTurnsAsync([turn], prevSha, keep);

        // Fige l'état restauré pour que le prochain tour ne récolte pas ces diffs.
        await _dev.CommitAllAsync($"retour: restauration sélective avant tour {turn.Id}");
        return $"Restauration avant le tour {turn.Id} : {summary}" +
               (keep.Count > 0 ? $" Conservés : {string.Join(", ", keep)}." : "");
    }

    // Restaure chaque fichier des tours retirés : depuis l'état d'avant la
    // période annulée (SHA précédent, sinon parent du premier tour retiré),
    // sinon suppression (fichier créé pendant la période annulée).
    private async Task<string> RevertTurnsAsync(
        IReadOnlyList<TurnRecord> turns, string? prevSha, ISet<string>? keepPaths)
    {
        var baseSha = prevSha;
        if (string.IsNullOrEmpty(baseSha))
        {
            foreach (var t in turns)
            {
                if (string.IsNullOrEmpty(t.GitShaAfter)) continue;
                baseSha = await _dev.ParentShaAsync(t.GitShaAfter);
                if (!string.IsNullOrEmpty(baseSha)) break;
            }
        }

        int restored = 0, deleted = 0, kept = 0;
        var errors = new List<string>();

        foreach (var t in turns)
        {
            foreach (var raw in t.ChangedFiles)
            {
                var file = NormalizePath(raw);
                if (file.Length == 0) continue;
                if (keepPaths is not null && keepPaths.Contains(file)) { kept++; continue; }

                try
                {
                    if (!string.IsNullOrEmpty(baseSha) && await _dev.FileExistsInAsync(baseSha, file))
                    {
                        if (await _dev.CheckoutFilesAsync(baseSha!, [file])) restored++;
                        else errors.Add(file);
                    }
                    else if (await _dev.FileExistsInAsync(null, file))
                    {
                        if (await _dev.DeleteFileAsync(file)) deleted++;
                        else errors.Add(file);
                    }
                    else restored++; // déjà absent : rien à faire, état cible atteint
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Historique] restauration de {File} impossible", file);
                    errors.Add(file);
                }
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(restored > 0 ? $"{restored} fichier(s) restauré(s)" : "");
        if (deleted > 0) sb.Append(sb.Length > 0 ? ", " : "").Append($"{deleted} fichier(s) créé(s) supprimé(s)");
        if (kept > 0) sb.Append(sb.Length > 0 ? ", " : "").Append($"{kept} conservé(s)");
        if (errors.Count > 0) sb.Append(". Échecs : ").Append(string.Join(", ", errors.Take(5)));
        if (sb.Length == 0) sb.Append("aucun changement de fichier concerné");
        return sb.ToString();
    }

    private static string NormalizePath(string p) => p.Trim().Replace('\\', '/').TrimStart('/');

    private int FindIndex(string turnId)
    {
        for (var i = 0; i < _turns.Count; i++)
            if (_turns[i].Id == turnId ||
                _turns[i].Id.StartsWith(turnId, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max];

    private void SaveLocked()
    {
        try { Security.SafeFileWriter.WriteText(_path, JsonSerializer.Serialize(_turns, JsonOpts)); }
        catch (Exception ex) { _logger?.LogWarning(ex, "[Historique] sauvegarde impossible"); }
    }

    private List<TurnRecord> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<TurnRecord>>(File.ReadAllText(_path), JsonOpts) ?? [];
        }
        catch { return []; }
    }
}

// ── Enregistreur des tours vocaux ─────────────────────────────────────────
/// <summary>
/// Abonne l'historique des tours au moteur vocal : chaque réponse réussie donnée
/// par la voix devient un tour annulable (« Jarvis, annule tes derniers changements »).
/// </summary>
public sealed class VoiceTurnRecorder : BackgroundService
{
    private readonly Lazy<VoiceConversationService> _voice;
    private readonly ITurnHistory _history;
    private readonly ILogger<VoiceTurnRecorder> _logger;
    private Action<VoiceUtteranceRecord>? _handler;

    public VoiceTurnRecorder(
        Lazy<VoiceConversationService> voice,
        ITurnHistory history,
        ILogger<VoiceTurnRecorder> logger)
    {
        _voice = voice;
        _history = history;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _handler = rec =>
            {
                if (!string.Equals(rec.Status, "success", StringComparison.OrdinalIgnoreCase)) return;
                if (string.IsNullOrWhiteSpace(rec.Transcript)) return;
                _ = Task.Run(async () =>
                {
                    try { await _history.RecordAsync("voice", rec.Transcript!, rec.Response); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Historique] tour vocal non enregistré");
                    }
                });
            };
            _voice.Value.UtteranceProcessed += _handler;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Historique] enregistreur vocal indisponible");
            return;
        }

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        try
        {
            if (_handler is not null) _voice.Value.UtteranceProcessed -= _handler;
        }
        catch { /* service jamais résolu */ }
        base.Dispose();
    }
}
