using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class InstagramTool : ITool
{
    private readonly IntegrationsStore _store;
    private readonly ILogger<InstagramTool> _logger;
    private readonly HttpClient _http;
    private string? _cachedHost;

    public InstagramTool(IntegrationsStore store, ILogger<InstagramTool> logger)
    {
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Name => "instagram";
    public string Description =>
        "Instagram Graph API multi-comptes : snapshot abonnés/vues vidéos + diff vs veille. " +
        "Actions : resume (snapshot+diff), refresh (force), list (comptes).";
    public string Category => "social";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "resume | refresh | list", typeof(string), required: true)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        var settings = _store.Get().Instagram;
        if (settings.Comptes.Count == 0)
            return ToolResult.Failed("Aucun compte Instagram configuré. Ajoute au moins un compte (Nom, UserId, AccessToken) dans les paramètres.");

        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "list" => Task.FromResult(ToolResult.Succeeded("COMPTES CONFIGURÉS :\n" +
                    string.Join("\n", settings.Comptes.Select(c => $"  • {c.Nom} (UID: {c.UserId})")))),
                "resume" => ResumeAsync(settings, ct),
                "refresh" => RefreshAsync(settings, ct),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : resume, refresh, list"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Instagram] {Action} échoué", action);
            return ToolResult.Failed($"Erreur Instagram : {ex.Message}");
        }
    }

    private async Task<ToolResult> ResumeAsync(InstagramSettings settings, CancellationToken ct)
    {
        var snapshots = LoadSnapshots();
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var allDeltas = new List<string>();

        foreach (var account in settings.Comptes)
        {
            var snap = await FetchAccountSnapshotAsync(account, ct);
            var key = account.UserId;
            if (!snapshots.ContainsKey(key)) snapshots[key] = new Dictionary<string, InstagramSnapshot>();
            var yesterday = snapshots[key].Keys.Where(d => DateTime.TryParse(d, out var dt) && dt.Date == DateTime.Today.AddDays(-1))
                .OrderByDescending(d => d).FirstOrDefault();

            snapshots[key][today] = snap;
            SaveSnapshots(snapshots);

            if (yesterday is not null && snapshots[key].TryGetValue(yesterday, out var prev))
            {
                var delta = ComputeDelta(prev, snap, account.Nom);
                if (!string.IsNullOrWhiteSpace(delta))
                    allDeltas.Add(delta);
            }
        }

        if (allDeltas.Count == 0)
            return ToolResult.Succeeded("Snapshot fait. Aucun changement vs veille (ou pas de données précédentes).");
        return ToolResult.Succeeded("INSTAGRAM — CHANGEMENTS VS VEILLE :\n" + string.Join("\n", allDeltas));
    }

    private async Task<ToolResult> RefreshAsync(InstagramSettings settings, CancellationToken ct)
    {
        foreach (var account in settings.Comptes)
        {
            await FetchAccountSnapshotAsync(account, ct);
            await Task.Delay(500, ct);
        }
        return ToolResult.Succeeded("Rafraîchissement forcé effectué.");
    }

    private async Task<InstagramSnapshot> FetchAccountSnapshotAsync(InstagramAccount acc, CancellationToken ct)
    {
        var host = await ResolveHostAsync(ct);
        var token = acc.AccessToken;

        // Profile
        var profUrl = $"https://{host}/{acc.UserId}?fields=username,followers_count,media_count&access_token={token}";
        using var profReq = new HttpRequestMessage(HttpMethod.Get, profUrl);
        using var profResp = await _http.SendAsync(profReq, ct);
        if (!profResp.IsSuccessStatusCode)
        {
            var err = await profResp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[Instagram] Profile fetch failed for {Nom}: {Err}", acc.Nom, err);
            return new InstagramSnapshot();
        }
        using var profStream = await profResp.Content.ReadAsStreamAsync(ct);
        var profDoc = await JsonDocument.ParseAsync(profStream, cancellationToken: ct);
        var root = profDoc.RootElement;
        var username = root.TryGetProperty("username", out var u) ? u.GetString() ?? acc.Nom : acc.Nom;
        var followers = root.TryGetProperty("followers_count", out var fc) ? fc.GetInt64() : 0;
        var mediaCount = root.TryGetProperty("media_count", out var mc) ? mc.GetInt32() : 0;

        // Recent media (limit 8)
        var mediaUrl = $"https://{host}/{acc.UserId}/media?fields=id,media_type,caption,timestamp,like_count,comments_count&limit=8&access_token={token}";
        using var mediaReq = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
        using var mediaResp = await _http.SendAsync(mediaReq, ct);
        var mediaItems = new List<InstagramMedia>();
        if (mediaResp.IsSuccessStatusCode)
        {
            using var mediaStream = await mediaResp.Content.ReadAsStreamAsync(ct);
            var mediaDoc = await JsonDocument.ParseAsync(mediaStream, cancellationToken: ct);
            if (mediaDoc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    var mid = item.GetProperty("id").GetString()!;
                    var views = await FetchViewsAsync(mid, host, token, ct);
                    mediaItems.Add(new InstagramMedia
                    {
                        Id = mid,
                        Type = item.TryGetProperty("media_type", out var mt) ? mt.GetString() ?? "" : "",
                        Views = views,
                        Timestamp = item.TryGetProperty("timestamp", out var ts) ? DateTime.TryParse(ts.GetString(), out var dt) ? dt : DateTime.MinValue : DateTime.MinValue
                    });
                }
            }
        }

        return new InstagramSnapshot
        {
            Username = username,
            Followers = followers,
            MediaCount = mediaCount,
            Medias = mediaItems
        };
    }

    private async Task<int> FetchViewsAsync(string mediaId, string host, string token, CancellationToken ct)
    {
        try
        {
            var url = $"https://{host}/{mediaId}/insights?metric=views&access_token={token}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return 0;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var first = data.EnumerateArray().First();
                if (first.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
                {
                    var v = vals.EnumerateArray().First();
                    if (v.TryGetProperty("value", out var val)) return val.GetInt32();
                }
            }
            // fallback plays
            url = $"https://{host}/{mediaId}/insights?metric=plays&access_token={token}";
            using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp2 = await _http.SendAsync(req2, ct);
            if (!resp2.IsSuccessStatusCode) return 0;
            using var stream2 = await resp2.Content.ReadAsStreamAsync(ct);
            var doc2 = await JsonDocument.ParseAsync(stream2, cancellationToken: ct);
            if (doc2.RootElement.TryGetProperty("data", out var data2) && data2.GetArrayLength() > 0)
            {
                var first = data2.EnumerateArray().First();
                if (first.TryGetProperty("values", out var vals2) && vals2.GetArrayLength() > 0)
                {
                    var v = vals2.EnumerateArray().First();
                    if (v.TryGetProperty("value", out var val2)) return val2.GetInt32();
                }
            }
        }
        catch { }
        return 0;
    }

    private async Task<string> ResolveHostAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedHost)) return _cachedHost;
        foreach (var host in new[] { "graph.facebook.com", "graph.instagram.com" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/?access_token=test");
                using var resp = await _http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    _cachedHost = host;
                    return host;
                }
            }
            catch { }
        }
        return "graph.facebook.com";
    }

    private static string ComputeDelta(InstagramSnapshot prev, InstagramSnapshot curr, string nom)
    {
        var lines = new List<string>();
        var folDiff = curr.Followers - prev.Followers;
        if (folDiff != 0) lines.Add($"  {nom} : abonnés {FormatDiff(folDiff)} ({curr.Followers:N0})");
        foreach (var m in curr.Medias)
        {
            var pm = prev.Medias.FirstOrDefault(x => x.Id == m.Id);
            if (pm is null)
            {
                lines.Add($"  {nom} : NOUVELLE VIDÉO {m.Id} ({m.Views:N0} vues)");
            }
            else if (m.Views != pm.Views)
            {
                lines.Add($"  {nom} : {m.Id} vues {FormatDiff(m.Views - pm.Views)} ({m.Views:N0})");
            }
        }
        return lines.Count == 0 ? "" : string.Join("\n", lines);
    }

    private static string FormatDiff(long d) => d > 0 ? $"+{d:N0}" : d.ToString("N0");

    private static Dictionary<string, Dictionary<string, InstagramSnapshot>> LoadSnapshots()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "instagram.json");
        if (!File.Exists(path)) return new Dictionary<string, Dictionary<string, InstagramSnapshot>>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, InstagramSnapshot>>>(json)
                ?? new Dictionary<string, Dictionary<string, InstagramSnapshot>>();
        }
        catch { return new Dictionary<string, Dictionary<string, InstagramSnapshot>>(); }
    }

    private static void SaveSnapshots(Dictionary<string, Dictionary<string, InstagramSnapshot>> snaps)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "instagram.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(snaps, new JsonSerializerOptions { WriteIndented = true });
        JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(path, json);
    }

    private sealed class InstagramSnapshot
    {
        public string Username { get; set; } = "";
        public long Followers { get; set; }
        public int MediaCount { get; set; }
        public List<InstagramMedia> Medias { get; set; } = new();
    }
    private sealed class InstagramMedia
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public int Views { get; set; }
        public DateTime Timestamp { get; set; }
    }
}




