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
    private const int MaxDescriptionLength = 80;

    public static string Build(IReadOnlyList<AIToolDefinition> tools)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder(1536);

        sb.AppendLine($"Tu es Jarvis, assistant PC Windows. Réponds en français. Date : {now:dddd d MMMM yyyy, HH:mm}.");
        sb.AppendLine();

        sb.AppendLine("STYLE — direct, bref, sans préambule. Fais avant de dire.");
        sb.AppendLine("OUTILS — question simple = pas d'outil. Un seul outil quand suffit. Max 2 tentatives par outil.");
        sb.AppendLine("computer_action = outil unique pour applications PC (ouvrir, cliquer, taper). Jamais browser pour du local.");
        sb.AppendLine("Image/vidéo : description telle quelle, pas de précisions. Si échec, dis l'erreur.");
        sb.AppendLine();

        sb.AppendLine("OUTILS :");
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
