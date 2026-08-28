using JarvisAI.Web.Services;
using System.Diagnostics;
using System.Windows;

namespace JarvisAI.Desktop;

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
            if (!string.IsNullOrEmpty(exe))
            {
                // Lance la NOUVELLE instance AVANT de se fermer. Elle détectera
                // l'instance courante via le mutex, mais comme on libère le mutex
                // juste après, elle deviendra first instance et ignorera l'ancienne.
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true
                });
            }
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
