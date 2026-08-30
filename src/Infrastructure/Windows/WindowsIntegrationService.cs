using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace JarvisAI.Infrastructure.Windows;

public interface IWindowsIntegrationService
{
    void CreateStartMenuShortcut();
    void RemoveStartMenuShortcut();
    void CreateDesktopShortcut();
    void RemoveDesktopShortcut();
    void SetAutoStart(bool enabled);
    bool IsAutoStartEnabled();
    void RegisterUninstall();
    void RemoveUninstall();
    void EnableSystemTray(IntPtr mainWindowHandle);
    void DisableSystemTray();
    bool IsInstalled();
    string GetInstallPath();
    void EnsureDirectories();
}

public sealed class WindowsIntegrationService : IWindowsIntegrationService
{
    private const string AppName = "JarvisAI";
    private const string AppDescription = "Jarvis AI - Assistant Vocal Intelligent";
    private const string ExeName = "JarvisAI.Desktop.exe";

    private readonly string _installPath;
    private readonly string _startMenuPath;
    private readonly string _desktopPath;
    private readonly string _uninstallKey;

    private IntPtr _trayIconHandle;
    private NOTIFYICONDATA _trayData;

    public WindowsIntegrationService()
    {
        _installPath = Path.GetDirectoryName(typeof(WindowsIntegrationService).Assembly.Location) ?? "";
        _startMenuPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppName);
        _desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        _uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;
    }

    public void CreateStartMenuShortcut()
    {
        try
        {
            Directory.CreateDirectory(_startMenuPath);

            var exePath = Path.Combine(_installPath, ExeName);
            var shortcutPath = Path.Combine(_startMenuPath, $"{AppName}.lnk");

            CreateShortcut(shortcutPath, exePath, _installPath, AppDescription, Path.Combine(_installPath, "icon.ico"));

            // Also create an uninstall shortcut
            var uninstallPath = Path.Combine(_startMenuPath, $"Désinstaller {AppName}.lnk");
            var uninstallExe = Path.Combine(_installPath, "uninstall.exe");
            if (File.Exists(uninstallExe))
            {
                CreateShortcut(uninstallPath, uninstallExe, _installPath, "Désinstaller Jarvis AI");
            }

            // Create subfolder for tools
            var toolsPath = Path.Combine(_startMenuPath, "Outils");
            Directory.CreateDirectory(toolsPath);

            // Voice setup shortcut
            var voiceSetupPath = Path.Combine(toolsPath, "Configuration Voice.lnk");
            CreateShortcut(voiceSetupPath, exePath, _installPath, "Configurer le mode vocal", arguments: "--voice-setup");

            // Diagnostic shortcut
            var diagPath = Path.Combine(toolsPath, "Diagnostic.lnk");
            CreateShortcut(diagPath, exePath, _installPath, "Lancer un diagnostic", arguments: "--diagnostic");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create Start Menu shortcut: {ex.Message}");
        }
    }

    public void RemoveStartMenuShortcut()
    {
        try
        {
            if (Directory.Exists(_startMenuPath))
                Directory.Delete(_startMenuPath, true);
        }
        catch { }
    }

    public void CreateDesktopShortcut()
    {
        try
        {
            var exePath = Path.Combine(_installPath, ExeName);
            var shortcutPath = Path.Combine(_desktopPath, $"{AppName}.lnk");
            CreateShortcut(shortcutPath, exePath, _installPath, AppDescription, Path.Combine(_installPath, "icon.ico"));
        }
        catch { }
    }

    public void RemoveDesktopShortcut()
    {
        try
        {
            var shortcutPath = Path.Combine(_desktopPath, $"{AppName}.lnk");
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
        }
        catch { }
    }

    public void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                RegistryKeyPermissionCheck.ReadWriteSubTree);

            if (key is null) return;

            if (enabled)
            {
                var exePath = Path.Combine(_installPath, ExeName);
                key.SetValue(AppName, $"\"{exePath}\" --autostart");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }

    public bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public void RegisterUninstall()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(_uninstallKey);
            if (key is null) return;

            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("Publisher", "Jarvis AI");
            key.SetValue("DisplayDescription", AppDescription);
            key.SetValue("InstallLocation", _installPath);

            var exePath = Path.Combine(_installPath, ExeName);
            key.SetValue("UninstallString", $"\"{Path.Combine(_installPath, "uninstall.exe")}\"");
            key.SetValue("QuietUninstallString", $"\"{Path.Combine(_installPath, "uninstall.exe")}\" /SILENT");
            key.SetValue("DisplayIcon", Path.Combine(_installPath, "icon.ico"));

            // Estimate size
            key.SetValue("EstimatedSize", EstimateFolderSize(_installPath) / 1024, RegistryValueKind.DWord);

            // No modify/repair
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    public void RemoveUninstall()
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(_uninstallKey, false);
        }
        catch { }
    }

    public void EnableSystemTray(IntPtr mainWindowHandle)
    {
        // System tray is handled via P/Invoke - simplified version
        // In production, use a proper WPF NotifyIcon or similar
    }

    public void DisableSystemTray()
    {
        // Cleanup tray icon
    }

    public bool IsInstalled()
    {
        // Check if running from Program Files or installed location
        var exePath = Environment.ProcessPath ?? "";
        return exePath.Contains("JarvisAI") &&
               (exePath.Contains("Program Files") ||
                exePath.Contains("AppData\\Local\\Programs") ||
                File.Exists(Path.Combine(_installPath, "uninstall.exe")));
    }

    public string GetInstallPath() => _installPath;

    public void EnsureDirectories()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);

        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(Path.Combine(appData, "logs"));
        Directory.CreateDirectory(Path.Combine(appData, "config"));
        Directory.CreateDirectory(Path.Combine(appData, "voice_cache"));
        Directory.CreateDirectory(Path.Combine(appData, "models"));
    }

    private static void CreateShortcut(string shortcutPath, string targetPath,
        string workingDirectory, string description, string? iconPath = null, string? arguments = null)
    {
        // Create shortcut using PowerShell (more reliable than COM)
        var psScript = $@"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut('{shortcutPath}')
$shortcut.TargetPath = '{targetPath}'
$shortcut.WorkingDirectory = '{workingDirectory}'
$shortcut.Description = '{description}'
{(iconPath is not null ? $"$shortcut.IconLocation = '{iconPath}'" : "")}
{(arguments is not null ? $"$shortcut.Arguments = '{arguments}'" : "")}
$shortcut.Save()
";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{psScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch
        {
            // Fallback: create a simple .bat launcher instead
            var batPath = Path.ChangeExtension(shortcutPath, ".bat");
            File.WriteAllText(batPath, $"@echo off\nstart \"\" \"{targetPath}\" {arguments}");
        }
    }

    private static long EstimateFolderSize(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    // P/Invoke for system tray (simplified)
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
}
