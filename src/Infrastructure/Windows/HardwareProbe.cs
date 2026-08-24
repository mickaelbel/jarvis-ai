using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JarvisAI.Infrastructure.Windows;

/// <summary>RAM + GPU/VRAM de la machine, pour adapter les suggestions de modèles.</summary>
public static class HardwareProbe
{
    public sealed record Machine(int RamGo, string? Gpu, int VramGo);

    public static Machine Detecter()
    {
        var ramGo = (int)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0);
        string? gpu = null;
        var vramGo = 0;
        try
        {
            // nvidia-smi : source fiable pour VRAM dédiée NVIDIA.
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var ligne = p.StandardOutput.ReadLine();
                p.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(ligne))
                {
                    var parts = ligne.Split(',');
                    gpu = parts[0].Trim();
                    if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var mib))
                        vramGo = (int)Math.Round(mib / 1024.0);
                }
            }
        }
        catch { /* pas de GPU NVIDIA */ }

        if (gpu is null)
        {
            try
            {
                var chercheur = new System.Management.ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
                foreach (var o in chercheur.Get())
                {
                    var nom = o["Name"]?.ToString();
                    if (nom is null || nom.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;
                    gpu = nom;
                    if (o["AdapterRAM"] is uint octets) vramGo = (int)(octets / 1073741824u);
                    break;
                }
            }
            catch { /* WMI indisponible */ }
        }

        return new Machine(ramGo, gpu, vramGo);
    }
}
