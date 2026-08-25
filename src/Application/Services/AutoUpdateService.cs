using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Application.Services;

/// <summary>
/// Vérifie et applique les mises à jour via un manifest JSON hébergé
/// (GitHub Releases, URL statique…). L'URL est configurable dans les
/// paramètres. Sans URL → désactivé proprement.
/// </summary>
public sealed class AutoUpdateService
{
    private const string SettingsKey = "autoUpdateUrl";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string? _manifestUrl;

    public AutoUpdateService(HttpClient http, string? manifestUrl)
    {
        _http = http;
        _manifestUrl = manifestUrl;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_manifestUrl);

    public async Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            using var response = await _http.GetAsync(_manifestUrl, ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOpts);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) ||
                string.IsNullOrWhiteSpace(manifest.SetupUrl))
                return null;
            if (Version.TryParse(currentVersion, out var current) &&
                Version.TryParse(manifest.Version, out var remote) &&
                remote > current)
                return new UpdateInfo(manifest.Version, manifest.SetupUrl, manifest.Changelog ?? "");
            return null;
        }
        catch { return null; }
    }

    public static void DownloadAndInstall(string setupUrl, Action<string>? log = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"JarvisAI-Update-{Guid.NewGuid():N}.exe");
        try
        {
            log?.Invoke($"[AutoUpdate] Téléchargement : {setupUrl}");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var bytes = http.GetAsync(setupUrl).GetAwaiter().GetResult().Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            File.WriteAllBytes(temp, bytes);
            log?.Invoke($"[AutoUpdate] Install silencieux : {temp}");
            Process.Start(new ProcessStartInfo(temp, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            log?.Invoke($"[AutoUpdate] Échec : {ex.Message}");
        }
    }

    public sealed record UpdateInfo(string Version, string SetupUrl, string Changelog);
    private sealed class UpdateManifest
    {
        public string? Version { get; set; }
        public string? SetupUrl { get; set; }
        public string? Changelog { get; set; }
    }
}
