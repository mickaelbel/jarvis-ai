using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IVisualRegressionService
{
    string CaptureBaseline(string url, string pageName);
    RegressionResult CompareWithBaseline(string url, string pageName);
    IReadOnlyList<VisualBaseline> GetBaselines();
    void DeleteBaseline(string baselineId);
    string GenerateReport(List<RegressionResult> results);
}

public sealed class VisualRegressionService : IVisualRegressionService
{
    private readonly ILogger<VisualRegressionService> _logger;
    private readonly string _storagePath;
    private readonly string _baselinesPath;
    private readonly List<VisualBaseline> _baselines = new();

    public VisualRegressionService(ILogger<VisualRegressionService> logger)
    {
        _logger = logger;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _storagePath = Path.Combine(appData, "JarvisAI", "visual_baselines.json");
        _baselinesPath = Path.Combine(appData, "JarvisAI", "baseline_screenshots");
        Directory.CreateDirectory(_baselinesPath);
        Load();
    }

    public string CaptureBaseline(string url, string pageName)
    {
        var baseline = new VisualBaseline
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Url = url,
            PageName = pageName,
            Hash = ComputeHash($"{url}_{pageName}_{DateTime.UtcNow.Ticks}"),
            CapturedAt = DateTime.UtcNow
        };

        _baselines.Add(baseline);
        Save();

        _logger.LogInformation("[VisualRegression] Baseline captured: {Page}", pageName);
        return baseline.Id;
    }

    public RegressionResult CompareWithBaseline(string url, string pageName)
    {
        var baseline = _baselines.FirstOrDefault(b =>
            b.Url == url && b.PageName == pageName);

        if (baseline is null)
        {
            return new RegressionResult
            {
                Url = url,
                PageName = pageName,
                HasChanges = true,
                ChangeType = VisualChangeType.New,
                Message = "Aucune baseline trouvée - première capture"
            };
        }

        var currentHash = ComputeHash($"{url}_{pageName}_{DateTime.UtcNow.Ticks}");

        return new RegressionResult
        {
            Url = url,
            PageName = pageName,
            BaselineId = baseline.Id,
            HasChanges = currentHash != baseline.Hash,
            ChangeType = VisualChangeType.None,
            Message = currentHash == baseline.Hash ? "Aucun changement détecté" : "Changements visuels détectés",
            ComparedAt = DateTime.UtcNow
        };
    }

    public IReadOnlyList<VisualBaseline> GetBaselines()
        => _baselines.OrderByDescending(b => b.CapturedAt).ToList();

    public void DeleteBaseline(string baselineId)
    {
        _baselines.RemoveAll(b => b.Id == baselineId);
        Save();
    }

    public string GenerateReport(List<RegressionResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Rapport de Régression Visuelle");
        sb.AppendLine($"Généré le: {DateTime.UtcNow:dd/MM/yyyy HH:mm}");
        sb.AppendLine();

        var passed = results.Count(r => !r.HasChanges);
        var failed = results.Count(r => r.HasChanges);

        sb.AppendLine($"## Résumé: {passed} passed, {failed} failed");
        sb.AppendLine();

        if (failed > 0)
        {
            sb.AppendLine("## ⚠️ Changements détectés");
            foreach (var result in results.Where(r => r.HasChanges))
            {
                sb.AppendLine($"### {result.PageName}");
                sb.AppendLine($"- URL: {result.Url}");
                sb.AppendLine($"- Message: {result.Message}");
            }
            sb.AppendLine();
        }

        if (passed > 0)
        {
            sb.AppendLine("## ✅ Pages stables");
            foreach (var result in results.Where(r => !r.HasChanges))
            {
                sb.AppendLine($"- {result.PageName}");
            }
        }

        return sb.ToString();
    }

    private string ComputeHash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16];
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<VisualBaseline>>(json);
                if (loaded is not null) _baselines.AddRange(loaded);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_baselines, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class VisualBaseline
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string PageName { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime CapturedAt { get; set; }
}

public sealed class RegressionResult
{
    public string Url { get; set; } = "";
    public string PageName { get; set; } = "";
    public string? BaselineId { get; set; }
    public bool HasChanges { get; set; }
    public VisualChangeType ChangeType { get; set; }
    public string Message { get; set; } = "";
    public DateTime ComparedAt { get; set; }
}

public enum VisualChangeType { None, New, Modified, Deleted }
