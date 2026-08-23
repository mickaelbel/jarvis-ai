using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace JarvisAI.Infrastructure.Windows;

/// <summary>
/// Capture l'écran principal en PNG (base du multimodal et des souvenirs visuels).
/// </summary>
public static class ScreenCaptureProbe
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "screenshots");

    /// <summary>Retourne le chemin de la capture, ou null en cas d'échec.</summary>
    public static string? Capture()
    {
        try
        {
            var largeur = GetSystemMetrics(SM_CXSCREEN);
            var hauteur = GetSystemMetrics(SM_CYSCREEN);
            if (largeur <= 0 || hauteur <= 0) return null;

            Directory.CreateDirectory(Dir);
            using var bmp = new Bitmap(largeur, hauteur);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(0, 0, 0, 0, new Size(largeur, hauteur));
            var path = Path.Combine(Dir, $"ecran_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            bmp.Save(path, ImageFormat.Png);
            PurgeAnciens(10);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static void PurgeAnciens(int aGarder)
    {
        try
        {
            var fichiers = new DirectoryInfo(Dir).GetFiles("*.png")
                .OrderByDescending(f => f.CreationTime).ToList();
            for (var i = aGarder; i < fichiers.Count; i++) fichiers[i].Delete();
        }
        catch { /* best-effort */ }
    }
}
