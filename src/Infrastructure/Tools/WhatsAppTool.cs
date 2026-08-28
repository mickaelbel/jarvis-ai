using JarvisAI.Application.Agents;
using JarvisAI.Application.Services;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Outil générique « whatsapp » : donne à n'importe quel agent de Jarvis la
/// capacité d'envoyer/recevoir des messages WhatsApp, de placer un appel vocal
/// (best-effort) et de lancer une conversation téléphonique autonome vers un
/// objectif. S'appuie sur l'abstraction réutilisable <see cref="IWhatsAppPhoneAgent"/>.
/// </summary>
public sealed class WhatsAppTool : ITool
{
    private readonly IWhatsAppPhoneAgent _agent;
    private readonly ILogger<WhatsAppTool> _logger;

    public WhatsAppTool(IWhatsAppPhoneAgent agent, ILogger<WhatsAppTool> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    public string Name => "whatsapp";
    public string Description =>
        "WhatsApp (Phone Agent) : communique avec un contact via WhatsApp.\n" +
        "Action 'send' : contact + message -> envoie un message texte.\n" +
        "Action 'read' : contact -> lit les derniers messages de la conversation.\n" +
        "Action 'call' : contact -> place un appel vocal (best-effort, audio par le PC).\n" +
        "Action 'hangup' : raccroche un appel en cours.\n" +
        "Action 'autonomous' : goal + contact -> mène une conversation TELEPHONIQUE autonome vers un objectif (prend RDV, info, réclamation, commande…). Réutilisable par tous les agents.";
    public string Category => "phone";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public string? WaitingPhrase => "J'utilise WhatsApp…";

    public bool McpExpose => false;
    public bool IsAvailable => _agent.IsAvailable;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "send | read | call | hangup | autonomous", typeof(string), required: true),
        new("contact", "Nom ou numéro WhatsApp du contact (E.164 +336... ou nom du répertoire)", typeof(string)),
        new("message", "Message texte à envoyer (action send)", typeof(string)),
        new("goal", "Objectif en langage naturel pour la conversation autonome (action autonomous)", typeof(string)),
        new("maxTurns", "Nombre max de tours (autonomous, défaut 12)", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        action = action?.ToLowerInvariant();
        var contact = parameters.GetValueOrDefault("contact");

        switch (action)
        {
            case "send":
            {
                var message = parameters.GetValueOrDefault("message");
                if (string.IsNullOrWhiteSpace(contact) || string.IsNullOrWhiteSpace(message))
                    return ToolResult.Failed("Paramètres contact et message requis (action send).");
                var r = await _agent.SendTextAsync(contact, message, ct);
                return r.Success ? ToolResult.Succeeded(r.Output) : ToolResult.Failed(r.Output);
            }

            case "read":
            {
                if (string.IsNullOrWhiteSpace(contact))
                    return ToolResult.Failed("Paramètre contact requis (action read).");
                var r = await _agent.ReadConversationAsync(contact, 10, ct);
                return r.Success ? ToolResult.Succeeded($"CONVERSATION WhatsApp [{contact}] :\n{r.Output}") : ToolResult.Failed(r.Output);
            }

            case "call":
            {
                if (string.IsNullOrWhiteSpace(contact))
                    return ToolResult.Failed("Paramètre contact requis (action call).");
                var r = await _agent.PlaceVoiceCallAsync(contact, ct);
                return r.Success ? ToolResult.Succeeded(r.Output) : ToolResult.Failed(r.Output);
            }

            case "hangup":
            {
                var r = await _agent.HangUpCallAsync(ct);
                return r.Success ? ToolResult.Succeeded(r.Output) : ToolResult.Failed(r.Output);
            }

            case "autonomous":
            {
                var goal = parameters.GetValueOrDefault("goal");
                if (string.IsNullOrWhiteSpace(contact) || string.IsNullOrWhiteSpace(goal))
                    return ToolResult.Failed("Paramètres goal et contact requis (action autonomous).");
                int maxTurns = 12;
                var mt = parameters.GetValueOrDefault("maxTurns");
                if (!string.IsNullOrWhiteSpace(mt) && int.TryParse(mt, out var parsed) && parsed > 0)
                    maxTurns = parsed;

                Action<string>? spoken = null;
                Action<string>? status = null;
                // Le canal de confirmation est géré en amont (l'app remplace ce
                // callback au besoin) : par défaut on demande un simple ack.
                Func<string, Task<bool>>? confirm = null;

                var report = await _agent.RunAutonomousConversationAsync(goal, contact, spoken, status, confirm, maxTurns, ct);
                if (!report.Success)
                    return ToolResult.Failed(report.ErrorMessage ?? "Conversation autonome échouée.");

                return ToolResult.Succeeded(
                    $"RÉSUMÉ DE L'APPEL : {report.Summary}\n\n— TRANSCRIPTION —\n{report.Transcript}");
            }

            default:
                return ToolResult.Failed($"Action inconnue : {action}. Valides : send, read, call, hangup, autonomous.");
        }
    }
}
