using JarvisAI.Web.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace JarvisAI.Desktop;

/// <summary>
/// Lance l'exécutable Jarvis dans un NOUVEAU job (CREATE_BREAKAWAY_FROM_JOB) lors
/// d'un redémarrage. Sans cela, la nouvelle instance hérite du job couronné par
/// KILL_ON_JOB_CLOSE et se fait tuer quand l'ancienne instance se ferme.
/// </summary>
internal static class ProcessLauncher
{
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_SHOWNORMAL = 1;
    private const int ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static bool LaunchDetached(string exePath)
    {
        var workingDir = Path.GetDirectoryName(exePath);
        var commandLine = "\"" + exePath + "\"";

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_SHOWNORMAL;

        if (CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_BREAKAWAY_FROM_JOB | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero,
                workingDir,
                ref si,
                out var pi))
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }

        var err = Marshal.GetLastWin32Error();
        App.Log("CreateProcess(breakaway) failed: " + err);

        // Repli : lancement normal (sera sans doute tué par KILL_ON_JOB_CLOSE, mais
        // mieux que rien) — ou tentative via shell si la ligne de commande échoue.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = workingDir
            });
            return true;
        }
        catch (Win32Exception wex)
        {
            App.Log("Fallback Process.Start failed: " + wex.Message);
            return false;
        }
    }
}

public sealed class DesktopAppLifecycle : IAppLifecycleService, JarvisAI.Infrastructure.Dev.ISelfDevLifecycle
{
    public void Reload()
    {
        App.Log("Rechargement demandé depuis l'interface.");
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            if (app.MainWindow is MainWindow window) window.Reload();
        });
    }

    public void Restart()
    {
        App.Log("Redémarrage demandé depuis l'interface.");
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                App.Log("Redémarrage : Environment.ProcessPath vide.");
                ShutdownApp();
                return;
            }

            // Lance la NOUVELLE instance dans un nouveau job (CREATE_BREAKAWAY_FROM_JOB)
            // pour qu'elle ne soit PAS tuée par KILL_ON_JOB_CLOSE quand l'instance
            // courante se ferme. On lance AVANT de se fermer.
            var launchOk = ProcessLauncher.LaunchDetached(exe);
            if (!launchOk)
                App.Log("Redémarrage : échec du lancement détaché.");
        }
        catch (Exception ex)
        {
            App.Log("Redémarrage : échec du lancement : " + ex);
        }

        // Supprime le fichier active-url AVANT de libérer le mutex : la nouvelle
        // instance ne verra pas d'URL existante et démarrera son propre serveur.
        App.ClearActiveUrl();
        // Libère le mutex AVANT l'arrêt pour que la nouvelle instance
        // soit immédiatement reconnue comme first instance (pas de 10s d'attente).
        App.ReleaseMutex();
        ShutdownApp();
    }

    public void Stop()
    {
        App.Log("Arrêt demandé depuis l'interface.");
        ShutdownApp();
    }

    private static void ShutdownApp()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() => app.Shutdown());
    }
}
