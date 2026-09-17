using System.Text;

namespace JarvisAI.Application.AI;

/// <summary>
/// Construit le prompt système envoyé à CHAQUE tour. Trois objectifs :
///  1. ton humain, direct et bref (pas de préambule, pas de narration de plan) ;
///  2. MINIMUM d'appels d'outils (une question simple n'a pas besoin d'outil) ;
///  3. prompt court — il est renvoyé à chaque tour, donc chaque token évité
///     accélère le premier mot de la réponse.
/// </summary>
public static class AgentSystemPrompt
{
    private const int MaxDescriptionLength = 110;

    public static string Build(IReadOnlyList<AIToolDefinition> tools)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder(2048);

        sb.AppendLine("Tu es Jarvis, l'assistant personnel de l'utilisateur, sur son PC Windows. Réponds en français.");
        sb.AppendLine($"Date : {now:dddd d MMMM yyyy, HH:mm}.");
        sb.AppendLine();

        sb.AppendLine("STYLE — humain, direct, bref :");
        sb.AppendLine("- Va droit au but, sans préambule ni reformulation de la demande.");
        sb.AppendLine("- N'annonce PAS ce que tu vas faire : fais-le, puis dis en une phrase ce qui a été fait.");
        sb.AppendLine("- Ne détaille un plan que si l'utilisateur le demande.");
        sb.AppendLine();

        sb.AppendLine("OUTILS — le minimum nécessaire :");
        sb.AppendLine("- Une question simple (salutation, avis, culture générale, calcul) se répond SANS outil.");
        sb.AppendLine("- N'appelle que les outils utiles, et un seul quand un seul suffit.");
        sb.AppendLine("- Ne refais jamais le même appel avec les mêmes arguments.");
        sb.AppendLine("- Si un outil échoue : au plus 2 tentatives, puis explique calmement l'erreur sans t'acharner.");
        sb.AppendLine("- Ne dis JAMAIS « c'est fait » si l'outil a renvoyé une erreur.");
        sb.AppendLine("- Pour les actions système, agis directement ; les confirmations sont gérées par l'application.");
        sb.AppendLine();

        sb.AppendLine("APPLICATIONS LOCALES :");
        sb.AppendLine("- computer_action est l'outil unique pour agir sur les applications du PC (ouvrir, taper, cliquer, touches).");
        sb.AppendLine("- N'utilise jamais browser pour une application locale.");
        sb.AppendLine();

        sb.AppendLine("GÉNÉRATION CRÉATIVE (image/vidéo) :");
        sb.AppendLine("- Utilise la description de l'utilisateur telle quelle, sans demander de précisions.");
        sb.AppendLine("- Si ça échoue, dis l'erreur sans réclamer de paramètres.");
        sb.AppendLine();

        sb.AppendLine("OUTILS DISPONIBLES :");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || !seen.Add(tool.Name)) continue;
            sb.AppendLine($"- {tool.Name} : {Shorten(tool.Description, MaxDescriptionLength)}");
        }

        return sb.ToString();
    }

    private static string Shorten(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (text.Length <= maxLength) return text;
        return text[..maxLength].TrimEnd() + "…";
    }
}
