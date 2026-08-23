using JarvisAI.Infrastructure.Dev;
using Xunit;

namespace JarvisAI.Tests;

/// <summary>
/// TurnHistoryService sur un faux dépôt git en mémoire : enregistrement des
/// tours, undo du dernier tour, rewind profond (suppression des fichiers créés)
/// et restauration sélective (« garde ce fichier-là »).
/// </summary>
public class TurnHistoryTests : IDisposable
{
    /// <summary>Git miniature : snapshots par SHA + worktree courant.</summary>
    private sealed class FakeGit : IGitTurnOps
    {
        public Dictionary<string, Dictionary<string, string>> Commits = new();
        public Dictionary<string, string> Worktree = new();
        public Dictionary<string, string?> Parents = new();
        public List<string> CommitMessages = new();
        private int _counter;

        public string? FindRepo() => @"C:\fake-repo";

        public Task<string?> CommitHeadShaAsync()
            => Task.FromResult(Commits.Count == 0 ? null : Commits.Keys.Last());

        public async Task<(string? Sha, bool WasDirty)> CommitAllAsync(string message)
        {
            await Task.Yield();
            var headSha = Commits.Count == 0 ? null : Commits.Keys.Last();
            var head = headSha is null ? [] : Commits[headSha];
            var dirty = Worktree.Count != head.Count || Worktree.Any(kv =>
                !head.TryGetValue(kv.Key, out var v) || v != kv.Value);
            if (!dirty) return (headSha, false);
            var sha = "sha" + (++_counter);
            Commits[sha] = new Dictionary<string, string>(Worktree);
            Parents[sha] = headSha;
            CommitMessages.Add(message);
            return (sha, true);
        }

        public Task<string?> ParentShaAsync(string sha)
            => Task.FromResult(Parents.TryGetValue(sha, out var p) ? p : null);

        public Task<List<string>> DiffNamesFromAsync(string? fromSha)
        {
            var head = Commits.Count == 0 ? new Dictionary<string, string>() : Commits[Commits.Keys.Last()];
            var baseSnap = fromSha is not null && Commits.TryGetValue(fromSha, out var b)
                ? b : [];
            var names = head.Concat(Worktree)
                .Where(kv => !baseSnap.TryGetValue(kv.Key, out var v) || v != kv.Value)
                .Select(kv => kv.Key)
                .Concat(baseSnap.Keys.Where(k => !head.ContainsKey(k) && !Worktree.ContainsKey(k)))
                .Distinct().ToList();
            return Task.FromResult(names);
        }

        public Task<bool> CheckoutTreeAsync(string sha)
        {
            if (!Commits.TryGetValue(sha, out var snap)) return Task.FromResult(false);
            Worktree = new Dictionary<string, string>(snap);
            return Task.FromResult(true);
        }

        public Task<bool> CheckoutFilesAsync(string sha, IReadOnlyList<string> files)
        {
            if (!Commits.TryGetValue(sha, out var snap)) return Task.FromResult(false);
            foreach (var f in files)
            {
                if (snap.TryGetValue(f, out var content)) Worktree[f] = content;
                else Worktree.Remove(f);
            }
            return Task.FromResult(true);
        }

        public Task<bool> FileExistsInAsync(string? sha, string path)
            => Task.FromResult(sha is null
                ? Worktree.ContainsKey(path)
                : Commits.TryGetValue(sha!, out var snap) && snap.ContainsKey(path));

        public Task<bool> DeleteFileAsync(string path)
            => Task.FromResult(Worktree.Remove(path));
    }

    private readonly FakeGit _git = new();
    private readonly TurnHistoryService _history;
    private readonly string _storePath;

    public TurnHistoryTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"turn-history-test-{Guid.NewGuid():N}.json");
        _history = new TurnHistoryService(_git, filePath: _storePath);
    }

    public void Dispose()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    [Fact]
    public async Task Record_Committe_Et_Trace_Les_Fichiers_Modifies()
    {
        // État initial committé
        _git.Worktree["src/a.cs"] = "v1";
        await _git.CommitAllAsync("initial");

        // Tour 1 : modification + création
        _git.Worktree["src/a.cs"] = "v1-mod";
        _git.Worktree["src/new/c.cs"] = "créé";
        var record = await _history.RecordAsync("chat", "ajoute une fonctionnalité", "fait");

        Assert.NotNull(record);
        Assert.Equal("chat", record!.Source);
        Assert.Contains("src/a.cs", record.ChangedFiles);
        Assert.Contains("src/new/c.cs", record.ChangedFiles);
        Assert.NotNull(record.GitShaAfter);
        Assert.Contains("tour chat:", _git.CommitMessages[^1]);
        Assert.Equal(1, _history.Count);
    }

    [Fact]
    public async Task UndoLast_Restore_Le_Contenu_Precedent()
    {
        _git.Worktree["src/a.cs"] = "v1";
        _git.Worktree["src/b.cs"] = "base";
        await _git.CommitAllAsync("initial");

        var t1 = await _history.RecordAsync("chat", "modifie a", null);
        _git.Worktree["src/a.cs"] = "v2";
        var t2 = await _history.RecordAsync("chat", "re-modifie a", null);
        _git.Worktree["src/b.cs"] = "cassé";
        await _history.RecordAsync("voice", "casse b", null);

        Assert.Equal("v2", _git.Worktree["src/a.cs"]);

        var info = await _history.UndoLastAsync(1); // annule le tour vocal
        Assert.Contains("restauré", info);
        Assert.Equal("base", _git.Worktree["src/b.cs"]);
        Assert.Equal("v2", _git.Worktree["src/a.cs"]); // t1/t2 intacts
        Assert.Equal(2, _history.Count);

        var info2 = await _history.UndoLastAsync(5); // annule tout (clampé)
        Assert.Equal("v1", _git.Worktree["src/a.cs"]);
        Assert.Equal(0, _history.Count);
    }

    [Fact]
    public async Task RewindBefore_Supprime_Les_Fichiers_Crees_Pendant_La_Periode()
    {
        _git.Worktree["src/a.cs"] = "v1";
        await _git.CommitAllAsync("initial");

        var t1 = await _history.RecordAsync("chat", "tour 1", null);
        _git.Worktree["src/generated/x.cs"] = "nouveau";
        var t2 = await _history.RecordAsync("chat", "tour 2 crée un fichier", null);

        var info = await _history.RewindBeforeAsync(t1!.Id);

        Assert.False(_git.Worktree.ContainsKey("src/generated/x.cs"), "le fichier créé doit être supprimé");
        Assert.Equal(0, _history.Count);
        Assert.StartsWith("Retour avant le tour", info);
    }

    [Fact]
    public async Task RestoreSelective_Conserve_Les_Chemins_Exemptes()
    {
        _git.Worktree["src/keep/me.cs"] = "original-keep";
        _git.Worktree["src/revert/me.cs"] = "original-revert";
        await _git.CommitAllAsync("initial");

        // Un seul tour modifie les deux fichiers
        _git.Worktree["src/keep/me.cs"] = "changé";
        _git.Worktree["src/revert/me.cs"] = "changé";
        await _history.RecordAsync("chat", "grosse refactorisation", null);

        var info = await _history.RestoreSelectiveAsync(
            backCount: 1,
            keepPaths: ["src/keep/me.cs"]);

        Assert.Equal("changé", _git.Worktree["src/keep/me.cs"]);   // conservé
        Assert.Equal("original-revert", _git.Worktree["src/revert/me.cs"]); // restauré
        Assert.Contains("Conservés", info);
    }

    [Fact]
    public async Task Historique_Vide_Et_Tour_Inconnu_Sont_Geres()
    {
        var empty = await _history.UndoLastAsync();
        Assert.Contains("Aucun tour", empty);

        var unknown = await _history.RewindBeforeAsync("zzzz");
        Assert.Contains("introuvable", unknown);

        // Tour sans aucun changement de fichier : enregistré sans crash
        _git.Worktree["src/a.cs"] = "v1";
        await _git.CommitAllAsync("initial");
        var clean = await _history.RecordAsync("chat", "simple question sans code", "réponse");
        Assert.NotNull(clean);
        Assert.Empty(clean!.ChangedFiles);
    }
}
