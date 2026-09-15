using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JarvisAI.Infrastructure.Models;

/// <summary>Charge estimée d'un modèle local (RAM + VRAM) lors du chargement.</summary>
public sealed record ModelCharge(
    string Nom,
    double RamGo,
    double VramGo,
    DateTimeOffset DernierUsage);

/// <summary>Budget mémoire global : RAM système + VRAM NVIDIA (nvidia-smi).</summary>
public sealed record ResourceBudget(
    double RamTotalGo,
    double RamUtiliseeGo,
    double VramTotalGo,
    double VramUtiliseeGo)
{
    public double RamDispoGo => Math.Max(0, RamTotalGo - RamUtiliseeGo);
    public double VramDispoGo => Math.Max(0, VramTotalGo - VramUtiliseeGo);

    /// <summary>Réserve conservatrice : ne jamais dépasser 90 % de la moyenne des deux mémoires.</summary>
    public double RamMarge => Math.Max(0, RamDispoGo - RamTotalGo * 0.10);
    public double VramMarge => Math.Max(0, VramDispoGo - VramTotalGo * 0.10);
}

/// <summary>
/// Gestionnaire de ressources des modèles locaux : sonde RAM système (GlobalMemoryStatusEx)
/// et VRAM NVIDIA (nvidia-smi), et aide à décider si un modèle peut être chargé puis,
/// sinon, lesquels décharger en priorité (LRU par dernier usage).
/// </summary>
public sealed class ModelResourceManager
{
    private readonly VramCatalogService _vram;
    private readonly ILogger<ModelResourceManager> _logger;

    public ModelResourceManager(VramCatalogService vram, ILogger<ModelResourceManager> logger)
    {
        _vram = vram;
        _logger = logger;
    }

    /// <summary>Budget courant RAM + VRAM.</summary>
    public ResourceBudget SondBudget()
    {
        var (ramTotal, ramUsed) = SondRam();
        var (_, vramTotal, vramUsed) = _vram.SondNvidiaSmiPublic();
        return new ResourceBudget(ramTotal, ramUsed, vramTotal, vramUsed);
    }

    /// <summary>
    /// Le modèle demandé rentre-t-il dans le budget (RAM + VRAM) ?
    /// Sans GPU NVIDIA détecté, seule la RAM contraint (le modèle tourne sur CPU).
    /// </summary>
    public bool PeutCharger(ModelCharge cible, ResourceBudget budget)
    {
        var graceRam = cible.RamGo <= budget.RamMarge;
        if (budget.VramTotalGo <= 0)
            return graceRam;
        return graceRam && cible.VramGo <= budget.VramMarge;
    }

    /// <summary>
    /// Choisit les modèles à décharger pour libérer la place nécessaire au chargement de la cible,
    /// par ordre LRU (le moins utilisé récemment d'abord). Retourne la liste des noms à décharger.
    /// Compatible avec l'état réel : seuls les modèles chargés comptent contre le budget.
    /// </summary>
    public IReadOnlyList<string> ChoisirDefchargement(
        ModelCharge cible,
        ResourceBudget budget,
        IReadOnlyList<ModelCharge> charges)
    {
        var aRetirer = new List<string>();
        if (PeutCharger(cible, budget) || charges.Count == 0)
            return aRetirer;

        double ramCumul = 0, vramCumul = 0;
        foreach (var charge in charges.OrderBy(c => c.DernierUsage))
        {
            if (PeutCharger(cible, budget with
                {
                    RamUtiliseeGo = Math.Max(0, budget.RamUtiliseeGo - ramCumul),
                    VramUtiliseeGo = Math.Max(0, budget.VramUtiliseeGo - vramCumul)
                }))
                break;

            aRetirer.Add(charge.Nom);
            ramCumul += charge.RamGo;
            vramCumul += charge.VramGo;
        }

        _logger.LogInformation(
            "[ModelResource] Déchargement recommandé pour « {Cible} » : {Noms}",
            cible.Nom,
            aRetirer.Count == 0 ? "aucun (impossible)" : string.Join(", ", aRetirer));

        return aRetirer;
    }

    private static (double TotalGo, double UtiliseeGo) SondRam()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return (0, 0);

        var total = status.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
        var free = status.ullAvailPhys / 1024.0 / 1024.0 / 1024.0;
        return (total, Math.Max(0, total - free));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}