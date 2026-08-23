using System.Diagnostics;

namespace JarvisAI.Infrastructure.Models;

// Catalogue de modèles locaux avec estimation VRAM (façon core/panneau.py du repo
// Python : « quels modèles rentrent dans ma carte ? »). Détection GPU via nvidia-smi,
// aucune dépendance externe. Estimations pour quantizations Q4 côté LLM.
public sealed record VramModel(
    string Nom,
    string Famille,
    double VramGo,
    string Usage,
    bool Recommande);

public sealed record VramStatus(
    string? GpuNom,
    double VramTotalGo,
    double VramUtiliseeGo,
    IReadOnlyList<(VramModel Modele, bool Rentre)> Catalogue);

public sealed class VramCatalogService
{
    private static readonly VramModel[] Catalogue =
    [
        // Voix
        new("Piper TTS", "voix", 0.2, "Synthèse voix FR", true),
        new("Whisper small", "voix", 1.2, "STT temps réel (ton venv voix)", true),
        new("Whisper medium", "voix", 2.8, "STT plus précis", false),
        new("Whisper large-v3", "voix", 6.0, "STT qualité max, plus lent", false),
        // LLM
        new("LLM 8B Q4", "llm", 6.0, "Mistral/Llama 8B quantizé — cerveau local", true),
        new("LLM 13B Q4", "llm", 9.5, "Meilleur raisonnement", false),
        new("LLM 34B Q4", "llm", 21.0, "Raisonnement avancé", false),
        new("LLM 70B Q4", "llm", 42.0, "Niveau cloud, GPU musclé requis", false),
        // Vision
        new("Vision OCR/Tesseract", "vision", 0.0, "OCR CPU, pas de VRAM", true),
        new("Modèle vision 4B Q4", "vision", 3.5, "Description d'écran locale", false),
        // Gestes / images
        new("MediaPipe gestes", "gestes", 0.3, "Tracking main webcam", true),
        new("Stable Diffusion 1.5", "images", 4.0, "Génération d'images rapide", false),
        new("SDXL", "images", 9.5, "Génération d'images qualité", false)
    ];

    public VramStatus Sond()
    {
        var (gpu, total, utilisee) = SondNvidiaSmi();
        var dispo = total - utilisee;
        var catalogue = Catalogue
            .Select(m => (Modele: m, Rentre: total <= 0 || m.VramGo <= Math.Max(dispo, total * 0.9)))
            .ToList();
        return new VramStatus(gpu, total, utilisee, catalogue);
    }

    private static (string? Gpu, double TotalGo, double UtiliseeGo) SondNvidiaSmi()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("nvidia-smi introuvable");

            var ligne = proc.StandardOutput.ReadLine();
            proc.WaitForExit(3000);
            if (string.IsNullOrWhiteSpace(ligne)) return (null, 0, 0);

            var parts = ligne.Split(',');
            if (parts.Length < 3) return (parts[0].Trim(), 0, 0);
            return (parts[0].Trim(),
                double.Parse(parts[1]) / 1024.0,
                double.Parse(parts[2]) / 1024.0);
        }
        catch
        {
            return (null, 0, 0); // pas de GPU NVIDIA détectable
        }
    }
}