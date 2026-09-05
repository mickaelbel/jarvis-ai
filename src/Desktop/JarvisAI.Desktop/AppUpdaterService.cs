using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace JarvisAI.Desktop;

public sealed class AppUpdaterService
{
    private readonly string _owner;
    private readonly string _repository;
    private readonly bool _autoInstall;
    private readonly string _localVersion;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_owner) && !string.IsNullOrWhiteSpace(_repository);
    public string Owner => _owner;
    public string Repository => _repository;
    public bool AutoInstall => _autoInstall;
    public string LocalVersion => _localVersion;

    private AppUpdaterService(string owner, string repository, bool autoInstall, string localVersion)
    {
        _owner = owner;
        _repository = repository;
        _autoInstall = autoInstall;
        _localVersion = localVersion;
    }

    public static AppUpdaterService Load()
    {
        var owner = "";
        var repo = "";
        var autoInstall = true;

        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("Update", out var update))
                {
                    owner = update.TryGetProperty("Owner", out var o) ? o.GetString() ?? "" : "";
                    repo = update.TryGetProperty("Repository", out var r) ? r.GetString() ?? "" : "";
                    autoInstall = !update.TryGetProperty("AutoInstall", out var a) || a.GetBoolean();
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("AppUpdater: lecture config impossible : " + ex.Message);
        }

        var localVersion = "";
        try
        {
            var vp = Path.Combine(AppContext.BaseDirectory, "app-version.txt");
            if (File.Exists(vp)) localVersion = File.ReadAllText(vp).Trim();
        }
        catch { }

        return new AppUpdaterService(owner, repo, autoInstall, localVersion);
    }

    public async Task<string?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        if (string.IsNullOrWhiteSpace(_localVersion))
        {
            App.Log("AppUpdater: pas de version locale — check ignoré.");
            return null;
        }

        try
        {
            var url = $"https://api.github.com/repos/{_owner}/{_repository}/releases/latest";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisAI-Updater/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            tag = tag?.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var remoteIsNewer = string.CompareOrdinal(tag, _localVersion) > 0;
            if (!remoteIsNewer)
            {
                App.Log($"AppUpdater: à jour (locale={_localVersion}, remote={tag}).");
                return null;
            }

            string? assetUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var download = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    if (name is not null && name.StartsWith("JarvisAI-Setup-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(download))
                    {
                        assetUrl = download;
                        break;
                    }
                }
            }

            App.Log($"AppUpdater: update {_localVersion} → {tag}" + (assetUrl is null ? " (aucun asset)" : ""));
            return assetUrl;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            App.Log("AppUpdater: check impossible : " + ex.Message);
            return null;
        }
    }

    public async Task<bool> DownloadAndInstallAsync(string assetUrl, CancellationToken ct = default)
    {
        try
        {
            var updatesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", "updates");
            Directory.CreateDirectory(updatesDir);

            var fileName = Path.GetFileName(new Uri(assetUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "JarvisAI-Setup-new.exe";
            var setupPath = Path.Combine(updatesDir, fileName);

            App.Log($"AppUpdater: téléchargement {assetUrl} → {setupPath}");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
            using var response = await client.GetAsync(assetUrl, ct);
            response.EnsureSuccessStatusCode();

            if (File.Exists(setupPath)) File.Delete(setupPath);
            await using (var fs = File.Create(setupPath))
            await using (var ms = await response.Content.ReadAsStreamAsync(ct))
                await ms.CopyToAsync(fs, ct);

            if (!File.Exists(setupPath) || new FileInfo(setupPath).Length < 100_000) return false;

            App.Log("AppUpdater: lancement installation silencieuse...");
            Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = true,
                WorkingDirectory = updatesDir,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
            });
            return true;
        }
        catch (Exception ex)
        {
            App.Log("AppUpdater: installation impossible : " + ex.Message);
            return false;
        }
    }
}
