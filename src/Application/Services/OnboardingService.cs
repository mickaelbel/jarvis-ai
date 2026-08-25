namespace JarvisAI.Application.Services;

/// <summary>
/// Gère le premier lancement de Jarvis : flag %LOCALAPPDATA%\JarvisAI\onboarded.dat.
/// Si absent, Jarvis se présente et guide l'utilisateur (micro, Chrome partagé…).
/// </summary>
public sealed class OnboardingService
{
    private static readonly string FlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "onboarded.dat");

    public bool IsFirstRun
    {
        get
        {
            try { return !File.Exists(FlagPath); }
            catch { return true; }
        }
    }

    public void MarkCompleted()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
            File.WriteAllText(FlagPath, DateTime.UtcNow.ToString("o"));
        }
        catch { }
    }

    /// <summary>Première séquence vocale à jouer au démarrage.</summary>
    public string GetWelcomeMessage() =>
        "Bonjour ! Je suis Jarvis, ton assistant de bureau. " +
        "Pour commencer, vérifie que ton micro est connecté, puis dis « Jarvis, bonjour » pour te présenter. " +
        "Si tu utilises un casque Bluetooth, assure-toi qu'il est allumé et connecté. " +
        "Tu peux me dire à tout moment : ouvre, dicte, rappelle, quelle heure est-il. " +
        "Amuse-toi bien !";
}
