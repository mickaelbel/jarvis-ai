using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Automation;

public interface IToolkitVersionCheckerService
{
    Task<ToolkitStatusReport> CheckAsync(CancellationToken ct = default);
}

public sealed class ToolkitVersionCheckerService : IToolkitVersionCheckerService
{
    private readonly ILogger<ToolkitVersionCheckerService> _logger;

    private static readonly Dictionary<string, (string Command, string[] Args, string? FallbackCommand, string[]? FallbackArgs)> ToolCommands = new()
    {
        ["node"] = ("node", new[] { "--version" }, null, null),
        ["npm"] = ("npm", new[] { "--version" }, null, null),
        ["python"] = ("python", new[] { "--version" }, "py", new[] { "--version" }),
        ["git"] = ("git", new[] { "--version" }, null, null),
        ["docker"] = ("docker", new[] { "--version" }, null, null),
        ["dotnet"] = ("dotnet", new[] { "--version" }, null, null),
    };

    private static readonly Dictionary<string, Version> LatestKnown = new()
    {
        ["node"] = new Version(22, 0),
        ["npm"] = new Version(10, 0),
        ["python"] = new Version(3, 12),
        ["git"] = new Version(2, 45),
        ["docker"] = new Version(27, 0),
        ["dotnet"] = new Version(8, 0),
    };

    private static readonly Regex VersionRegex = new(@"(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

    public ToolkitVersionCheckerService(ILogger<ToolkitVersionCheckerService> logger)
    {
        _logger = logger;
    }

    public async Task<ToolkitStatusReport> CheckAsync(CancellationToken ct = default)
    {
        var report = new ToolkitStatusReport { CheckedAt = DateTime.UtcNow };

        foreach (var tool in ToolCommands)
        {
            var info = await CheckToolAsync(tool.Key, tool.Value, ct);
            report.Tools.Add(info);
        }

        report.InstalledCount = report.Tools.Count(t => t.Installed);
        report.OutdatedCount = report.Tools.Count(t => t.Installed && t.IsOutdated);

        _logger.LogInformation("[ToolkitChecker] Vérifié {Installed}/{Total} outils, {Outdated} obsolètes",
            report.InstalledCount, report.Tools.Count, report.OutdatedCount);

        return report;
    }

    private async Task<ToolkitVersionInfo> CheckToolAsync(string name, (string Command, string[] Args, string? FallbackCommand, string[]? FallbackArgs) config, CancellationToken ct)
    {
        var info = new ToolkitVersionInfo { Name = name };

        var (output, success) = await TryRunAsync(config.Command, config.Args, ct);

        if (!success && config.FallbackCommand is not null)
        {
            (output, success) = await TryRunAsync(config.FallbackCommand, config.FallbackArgs!, ct);
        }

        if (!success || string.IsNullOrWhiteSpace(output))
        {
            info.Installed = false;
            info.UpdateProposal = $"{name} n'est pas installé";
            return info;
        }

        info.Installed = true;
        var versionStr = ExtractVersion(output);

        if (versionStr is not null && Version.TryParse(versionStr, out var version))
        {
            info.ActualVersion = versionStr;

            if (LatestKnown.TryGetValue(name, out var latest))
            {
                info.LatestKnownVersion = $"{latest.Major}.{latest.Minor}";

                if (version < latest)
                {
                    info.IsOutdated = true;
                    info.UpdateProposal = $"Mettre à jour {name} : {version.Major}.{version.Minor}.x → {latest.Major} {latest.Minor}.x LTS";
                }
                else
                {
                    info.UpdateProposal = $"{name} est à jour ({versionStr})";
                }
            }
            else
            {
                info.LatestKnownVersion = "inconnue";
                info.UpdateProposal = $"{name} : version {versionStr} détectée";
            }
        }
        else
        {
            info.ActualVersion = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "inconnue";
            info.LatestKnownVersion = "inconnue";
            info.UpdateProposal = $"{name} : version non parsable";
        }

        return info;
    }

    private static string? ExtractVersion(string output)
    {
        var match = VersionRegex.Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<(string Output, bool Success)> TryRunAsync(string command, string[] args, CancellationToken ct)
    {
        try
        {
            var arguments = string.Join(' ', args);
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process is null) return ("", false);

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (output + error, process.ExitCode == 0);
        }
        catch
        {
            return ("", false);
        }
    }
}

public sealed class ToolkitStatusReport
{
    public DateTime CheckedAt { get; set; }
    public List<ToolkitVersionInfo> Tools { get; set; } = new();
    public int InstalledCount { get; set; }
    public int OutdatedCount { get; set; }
}

public sealed class ToolkitVersionInfo
{
    public string Name { get; set; } = "";
    public bool Installed { get; set; }
    public string? ActualVersion { get; set; }
    public string? LatestKnownVersion { get; set; }
    public bool IsOutdated { get; set; }
    public string? UpdateProposal { get; set; }
}
