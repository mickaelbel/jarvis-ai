namespace JarvisAI.Application.Security;

public enum OperationMode
{
    Safe,
    Autonomous
}

public sealed class SecurityOptions
{
    public bool RequireConfirmationForHighRisk { get; set; } = true;
    public bool RequireConfirmationForMediumRisk { get; set; } = false;
    public bool VoiceConfirmationEnabled { get; set; } = false;
    public bool AllowDisableConfirmation { get; set; } = false;
    public int MaxActionsPerMinute { get; set; } = 30;
    public int DangerousToolTimeoutSeconds { get; set; } = 30;
    public OperationMode Mode { get; set; } = OperationMode.Safe;
    public IReadOnlySet<string> AllowedPaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Environment.GetFolderPath(Environment.SpecialFolder.Favorites),
        Environment.GetFolderPath(Environment.SpecialFolder.Recent),
        Path.GetTempPath()
    };
    public HashSet<string> BlacklistedCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm -rf", "format", "del /s", "del /f /s /q", "shutdown", "reboot",
        "dd if=", "mkfs", "> /dev/sda", "dism", "sfc /scannow",
        "reg delete", "reg add", "cacls", "icacls", "takeown",
        "diskpart", "bcdedit", "wmic /delete", "rd /s /q", "format c:"
    };
    public HashSet<string> WhitelistedTools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, the terminal tool rejects commands containing shell chaining
    /// metacharacters (&, |, ;, &gt;, &lt;, `, ^), line breaks or expansion markers.
    /// </summary>
    public bool EnableCommandInjectionGuard { get; set; } = true;

    /// <summary>
    /// Optional whitelist of allowed terminal commands. When empty, all commands
    /// are allowed (subject to the injection guard and blacklist). The command is
    /// matched by its executable name (e.g. "git", "npm", "dotnet").
    /// </summary>
    public HashSet<string> AllowedTerminalCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Les actions critiques (N3 : extinction/redémarrage du PC…) exigent une
    /// confirmation à chaque fois, même en mode autonome. Mettre à false pour un
    /// contrôle total sans confirmation (déconseillé).
    /// </summary>
    public bool N3RequiresConfirmation { get; set; } = true;

    public JarvisAI.Domain.Security.PermissionLevel DefaultPermissionLevel { get; set; } = JarvisAI.Domain.Security.PermissionLevel.User;
}