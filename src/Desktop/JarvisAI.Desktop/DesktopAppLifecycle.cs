using JarvisAI.Web.Services;
using System.Diagnostics;
using System.Windows;

namespace JarvisAI.Desktop;

public sealed class DesktopAppLifecycle : IAppLifecycleService
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
            if (!string.IsNullOrEmpty(exe))
            {
                // Lance un helper PowerShell qui attend la fermeture complète du
                // processus avant de relancer l'application. Cela libère le mutex
                // single-instance et les fichiers verrouillés (memory.db).
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments =
                        $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
                        $"-Command \"Wait-Process -Id {Environment.ProcessId}; Start-Process -FilePath '{exe}'\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
        }
        catch (Exception ex)
        {
            App.Log("Redémarrage : échec du lancement : " + ex);
        }
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
