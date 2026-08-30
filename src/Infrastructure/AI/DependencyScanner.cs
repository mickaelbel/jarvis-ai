using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AI;

public interface IDependencyScanner
{
    Task<ScanResult> ScanProjectAsync(string projectPath, CancellationToken ct = default);
    Task<ScanResult> ScanPackageAsync(string packageName, string version, CancellationToken ct = default);
    IReadOnlyList<VulnerabilityDatabase> GetDatabases();
}

public sealed class DependencyScanner : IDependencyScanner
{
    private readonly ILogger<DependencyScanner> _logger;
    private readonly HttpClient _httpClient;

    private static readonly Dictionary<string, string> KnownVulnerabilities = new()
    {
        ["Newtonsoft.Json"] = "CVE-2024-21907",
        ["System.Net.Http"] = "CVE-2024-21319",
        ["System.Text.RegularExpressions"] = "CVE-2019-0820",
        ["Microsoft.AspNetCore.Mvc.Core"] = "CVE-2024-21310",
        ["System.Security.Cryptography.Xml"] = "CVE-2024-21315",
    };

    public DependencyScanner(ILogger<DependencyScanner> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<ScanResult> ScanProjectAsync(string projectPath, CancellationToken ct = default)
    {
        var packages = await ExtractPackagesAsync(projectPath, ct);
        var vulnerabilities = new List<PackageVulnerability>();

        foreach (var pkg in packages)
        {
            var vuln = await CheckVulnerabilityAsync(pkg.Name, pkg.Version, ct);
            if (vuln is not null)
                vulnerabilities.Add(vuln);
        }

        var result = new ScanResult
        {
            ProjectPath = projectPath,
            TotalPackages = packages.Count,
            VulnerablePackages = vulnerabilities.Count,
            Vulnerabilities = vulnerabilities,
            RiskLevel = CalculateRiskLevel(vulnerabilities),
            ScannedAt = DateTime.UtcNow
        };

        _logger.LogInformation("[DepScan] {Path}: {Vulns}/{Total} vulnerabilities ({Risk})",
            Path.GetFileName(projectPath), vulnerabilities.Count, packages.Count, result.RiskLevel);

        return result;
    }

    public async Task<ScanResult> ScanPackageAsync(string packageName, string version, CancellationToken ct = default)
    {
        var vuln = await CheckVulnerabilityAsync(packageName, version, ct);
        var vulnerabilities = vuln is not null ? new List<PackageVulnerability> { vuln } : new();

        return new ScanResult
        {
            TotalPackages = 1,
            VulnerablePackages = vulnerabilities.Count,
            Vulnerabilities = vulnerabilities,
            RiskLevel = CalculateRiskLevel(vulnerabilities),
            ScannedAt = DateTime.UtcNow
        };
    }

    public IReadOnlyList<VulnerabilityDatabase> GetDatabases()
    {
        return new[]
        {
            new VulnerabilityDatabase { Name = "NIST NVD", Url = "https://nvd.nist.gov", Type = "National" },
            new VulnerabilityDatabase { Name = "GitHub Advisory", Url = "https://github.com/advisories", Type = "Community" },
            new VulnerabilityDatabase { Name = "NuGet Advisory", Url = "https://www.nuget.org/advisories", Type = "Package" }
        };
    }

    private async Task<List<PackageInfo>> ExtractPackagesAsync(string projectPath, CancellationToken ct)
    {
        var packages = new List<PackageInfo>();

        try
        {
            if (File.Exists(projectPath) && projectPath.EndsWith(".csproj"))
            {
                var content = await File.ReadAllTextAsync(projectPath, ct);
                var matches = System.Text.RegularExpressions.Regex.Matches(content,
                    @"<PackageReference\s+Include=""([^""]+)""\s+Version=""([^""]+)""");

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    packages.Add(new PackageInfo
                    {
                        Name = match.Groups[1].Value,
                        Version = match.Groups[2].Value
                    });
                }
            }
            else if (Directory.Exists(projectPath))
            {
                var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories);
                foreach (var csproj in csprojFiles)
                {
                    var subPackages = await ExtractPackagesAsync(csproj, ct);
                    packages.AddRange(subPackages);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DepScan] Error extracting packages");
        }

        return packages;
    }

    private async Task<PackageVulnerability?> CheckVulnerabilityAsync(string packageName, string version, CancellationToken ct)
    {
        // Check known vulnerabilities
        foreach (var kv in KnownVulnerabilities)
        {
            if (packageName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                return new PackageVulnerability
                {
                    PackageName = packageName,
                    InstalledVersion = version,
                    VulnerabilityId = kv.Value,
                    Severity = VulnerabilitySeverity.Medium,
                    Description = $"Known vulnerability in {kv.Key}",
                    Recommendation = $"Mettre à jour {kv.Key} vers la dernière version"
                };
            }
        }

        // Simulate checking external database
        await Task.Delay(10, ct);
        return null;
    }

    private VulnerabilityRiskLevel CalculateRiskLevel(List<PackageVulnerability> vulnerabilities)
    {
        if (vulnerabilities.Any(v => v.Severity == VulnerabilitySeverity.Critical))
            return VulnerabilityRiskLevel.Critical;
        if (vulnerabilities.Any(v => v.Severity == VulnerabilitySeverity.High))
            return VulnerabilityRiskLevel.High;
        if (vulnerabilities.Count > 0)
            return VulnerabilityRiskLevel.Medium;
        return VulnerabilityRiskLevel.Low;
    }
}

public sealed class ScanResult
{
    public string ProjectPath { get; set; } = "";
    public int TotalPackages { get; set; }
    public int VulnerablePackages { get; set; }
    public List<PackageVulnerability> Vulnerabilities { get; set; } = new();
    public VulnerabilityRiskLevel RiskLevel { get; set; }
    public DateTime ScannedAt { get; set; }
}

public sealed class PackageVulnerability
{
    public string PackageName { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string VulnerabilityId { get; set; } = "";
    public VulnerabilitySeverity Severity { get; set; }
    public string Description { get; set; } = "";
    public string? Recommendation { get; set; }
}

public sealed class PackageInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class VulnerabilityDatabase
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Type { get; set; } = "";
}

public enum VulnerabilitySeverity { Info, Low, Medium, High, Critical }
public enum VulnerabilityRiskLevel { Low, Medium, High, Critical }
