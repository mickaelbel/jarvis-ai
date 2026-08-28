using JarvisAI.Application.AI;
using JarvisAI.Application.Security;
using JarvisAI.Application.Services;
using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.Integrations.WhatsApp;

/// <summary>
/// Agent téléphonique WhatsApp autonome : remplit l'abstraction générique
/// <see cref="IWhatsAppPhoneAgent"/> en menant une conversation vers un objectif
/// via un moteur de raisonnement LLM, la sortie voix (TTS) et la confirmation
/// humaine pour les actions sensibles.
/// Réutilisable par tous les agents/outils de Jarvis.
/// </summary>
public sealed class WhatsAppPhoneAgent : IWhatsAppPhoneAgent
{
    private readonly WhatsAppWebDriver _driver;
    private readonly Lazy<IAIService> _ai;
    private readonly ILogger<WhatsAppPhoneAgent> _logger;
    private readonly IUserConfirmationService? _confirmation;
    private readonly Lazy<VoiceConversationService>? _voice;

    public WhatsAppPhoneAgent(
        WhatsAppWebDriver driver,
        Lazy<IAIService> ai,
        ILogger<WhatsAppPhoneAgent> logger,
        IUserConfirmationService? confirmation = null,
        Lazy<VoiceConversationService>? voice = null)
    {
        _driver = driver;
        _ai = ai;
        _logger = logger;
        _confirmation = confirmation;
        _voice = voice;
    }

    public string ChannelName => "WhatsApp";
    public bool IsAvailable => _driver.IsAvailable;

    public Task<WhatsAppResult> EnsureContactAsync(string contact, CancellationToken ct = default)
    {
        // La joignabilité est vérifiée lors des opérations concrètes (QR).
        return Task.FromResult(WhatsAppResult.Ok("Driver WhatsApp prêt. La connexion réelle se confirme à l'envoi."));
    }

    public Task<WhatsAppResult> SendTextAsync(string contact, string message, CancellationToken ct = default)
        => _driver.SendTextAsync(contact, message, ct);

    public Task<WhatsAppResult> ReadConversationAsync(string contact, int maxMessages = 10, CancellationToken ct = default)
        => _driver.ReadConversationAsync(contact, maxMessages, ct);

    public Task<WhatsAppResult> WaitForReplyAsync(string contact, TimeSpan timeout, CancellationToken ct = default)
        => _driver.WaitForReplyAsync(contact, timeout, ct);

    public Task<WhatsAppResult> PlaceVoiceCallAsync(string contact, CancellationToken ct = default)
        => _driver.PlaceVoiceCallAsync(contact, ct);

    public Task<WhatsAppResult> HangUpCallAsync(CancellationToken ct = default)
        => _driver.HangUpCallAsync(ct);

    public async Task<WhatsAppConversationReport> RunAutonomousConversationAsync(
        string goal,
        string contact,
        Action<string>? onSpoken = null,
        Action<string>? onStatus = null,
        Func<string, Task<bool>>? onConfirmationRequest = null,
        int maxTurns = 12,
        CancellationToken ct = default)
    {
        var transcript = new StringBuilder();
        void Log(string line) => transcript.AppendLine(line);

        // Sortie voix par défaut : Jarvis parle via la pile TTS locale.
        onSpoken ??= text =>
        {
            _logger.LogInformation("[WhatsAppAgent] 🗣 {Text}", text);
            try
            {
                // L'utilisateur entend l'avancement de l'appel (best-effort).
                if (_voice is not null) _ = SpeaksAway(text);
                else _logger.LogDebug("[WhatsAppAgent] Pas de canal TTS — affichage seul");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[WhatsAppAgent] TTS échec"); }
        };

        // Confirmation par défaut : canal existant (Web/Desktop) retourne dans
        // le cas où l'appel vient d'un outil (sans UI dédiée, on ne prend PAS
        // de risque : une action sensible non confirmée est refusée).
        Func<string, Task<bool>> defaultConfirm = async action =>
        {
            onStatus?.Invoke($"⚠ Confirmation requise : {action}");
            if (_voice is not null)
            {
                var answer = await ((IVoiceConfirmationChannel)_voice.Value).AskAsync(
                    $"L'action suivante nécessite votre accord : {action}. Dites oui pour valider, non pour refuser.",
                    TimeSpan.FromSeconds(30),
                    ct);
                if (answer is not null)
                    return answer.Accepted;
            }
            if (_confirmation is not null)
            {
                var res = await _confirmation.RequestConfirmationAsync(
                    new ConfirmationRequest(
                        "whatsapp.sensitive",
                        $"Autoriser l'action WhatsApp : {action} ?",
                        "high",
                        Guid.NewGuid(),
                        new Dictionary<string, string> { ["action"] = action }),
                    ct);
                return res.Confirmed;
            }
            return false;
        };
        onConfirmationRequest ??= defaultConfirm;

        try
        {
            onStatus?.Invoke($"Démarrage de l'appel WhatsApp vers {contact}…");
            var login = await _driver.EnsureLoggedInAsync(ct);
            if (!login.Success)
                return new WhatsAppConversationReport(false, transcript.ToString(), string.Empty, login.Output);

            // Optionnel : tentative d'appel vocal best-effort au démarrage.
            var call = await _driver.PlaceVoiceCallAsync(contact, ct);
            onStatus?.Invoke(call.Output);

            var conversation = new AIConversation(BuildSystemPrompt(goal, contact));
            Log($"OBJECTIF : {goal}");
            Log($"CONTACT : {contact}");

            var turns = 0;
            var waitingForReply = false;
            var doneSummary = string.Empty;

            while (turns < maxTurns && !ct.IsCancellationRequested)
            {
                turns++;

                // 1) Récupère l'état actuel de la conversation (réponses de l'interlocuteur).
                string incoming = string.Empty;
                if (waitingForReply)
                {
                    onStatus?.Invoke($"Attente d'une réponse de {contact}…");
                    var reply = await _driver.WaitForReplyAsync(contact, TimeSpan.FromSeconds(40), ct);
                    waitingForReply = false;
                    if (reply.Success)
                    {
                        incoming = reply.Output;
                        Log($"interlocuteur : {incoming}");
                        onSpoken?.Invoke($"{contact} : {incoming}");
                    }
                    else
                    {
                        // Timout de réponse : on le signale au LLM pour qu'il réagisse.
                        incoming = "(silence / pas de réponse dans le délai)";
                    }
                }
                else
                {
                    var read = await _driver.ReadConversationAsync(contact, 6, ct);
                    if (read.Success)
                        incoming = read.Output;
                }

                // On injecte uniquement ce qui est nouveau (message de l'interlocuteur).
                if (!string.IsNullOrWhiteSpace(incoming))
                    conversation.AddUserMessage($"Réponse de l'interlocuteur ({contact}) :\n{incoming}");

                // 2) Demande au LLM la prochaine action.
                var aiResp = await _ai.Value.ChatAsync(
                    $"Continue la conversation pour atteindre l'objectif. Utilise les balises du prompt et ne réponds QUE par une seule balise à la fois.",
                    conversation,
                    mode: ModelSelectionMode.Powerful,
                    cancellationToken: ct);

                if (!aiResp.Success)
                {
                    var err = aiResp.ErrorMessage ?? "erreur LLM";
                    _logger.LogWarning("[WhatsAppAgent] LLM erreur: {Err}", err);
                    onStatus?.Invoke($"Erreur du moteur : {err}");
                    return new WhatsAppConversationReport(false, transcript.ToString(), string.Empty, err);
                }

                var instruction = aiResp.Content.Trim();
                Log($"agent intrigua : {instruction}");

                // 3) Exécute l'action demandée par la balise.
                var handled = await ExecuteInstructionAsync(
                    instruction, contact, conversation, onSpoken, onStatus,
                    onConfirmationRequest, ct);

                if (handled == InstructionOutcome.Wait)
                {
                    waitingForReply = true;
                    continue;
                }
                if (handled == InstructionOutcome.Done)
                {
                    doneSummary = ExtractDoneSummary(instruction, aiResp.Content);
                    onStatus?.Invoke("Objectif atteint — fin de l'appel.");
                    break;
                }
                if (handled == InstructionOutcome.Abort)
                {
                    onStatus?.Invoke("Appel annulé (erreur d'exécution).");
                    break;
                }
                // Speak: continue en attendant la réponse (sauf si l'agent a dit DONE).
            }

            if (!ct.IsCancellationRequested && string.IsNullOrEmpty(doneSummary) && turns >= maxTurns)
                doneSummary = "Nombre maximal de tours atteint sans conclusion explicite du LLM.";

            await _driver.HangUpCallAsync(ct);

            var final = string.IsNullOrWhiteSpace(doneSummary)
                ? "Conversation terminée."
                : doneSummary;
            onSpoken?.Invoke(final);

            return new WhatsAppConversationReport(true, transcript.ToString(), final);
        }
        catch (OperationCanceledException)
        {
            return new WhatsAppConversationReport(false, transcript.ToString(), "Appel annulé.", "Annulé");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WhatsAppAgent] Échec de la conversation autonome");
            return new WhatsAppConversationReport(false, transcript.ToString(), "Conversation interrompue.", ex.Message);
        }
    }

    private static string BuildSystemPrompt(string goal, string contact) =>
        $"""
        Tu es un agent téléphonique d'assistance (Phone Agent) de Jarvis, l'assistant de l'utilisateur.
        Tu mènes une conversation WhatsApp autonome avec « {contact} » pour atteindre un OBJECTIF donné.
        Tu te présentes comme l'assistant de l'utilisateur et tu expliques la raison du contact.

        OBJECTIF À ATTEINDRE :
        {goal}

        RÈGLES DE CONDUITE :
        - Reste poli, clair, concis. Parle en français du locuteur (en général français).
        - Maintiens le contexte et l'objectif en tête pendant toute la conversation.
        - Tu ne dois répondre QUE par UNE SEULE balise à la fois, au format suivant :

        [SPEAK] texte à dire : texte à envoyer comme message à {contact} (une apparition).
        [WAIT] : attends maintenant une réponse de l'interlocuteur sans parler.
        [CONFIRM] description de l'action sensible : avant tout paiement, donnée sensible, engagement
            ou décision importante, interromps-toi et demande la validation de l'utilisateur. Décris
            précisément l'action proposée.
        [DONE] résumé clair : objectif accompli. Le résumé doit rappeler ce qui a été dit, ce qui a été
            fait et les éventuelles actions restantes.

        GESTION DES IMPRÉVUS :
        - Transferts / menus / attente : [SPEAK] une relance polie puis [WAIT] (ex: « Je patiente, prenez votre temps. »).
        - Proposition d'alternative de la part de l'interlocuteur : [CONFIRM] pour demander l'autorisation
          d'accepter l'alternative avant de continuer.
        - (silence / pas de réponse) : fais une relance [SPEAK], puis si cela se répète, conclus proprement avec [DONE].
        - Ne demande jamais d'info sensible directement à l'interlocuteur sans la validation de l'utilisateur.

        HISTORIQUE : tu recevras les messages précédents comme contexte Conversation. Adapte-toi.
        """;

    private enum InstructionOutcome { Speak, Wait, Done, Abort }

    private async Task<InstructionOutcome> ExecuteInstructionAsync(
        string instruction, string contact, AIConversation conversation,
        Action<string>? onSpoken, Action<string>? onStatus,
        Func<string, Task<bool>>? onConfirmationRequest, CancellationToken ct)
    {
        if (instruction.StartsWith("[WAIT]", StringComparison.OrdinalIgnoreCase))
        {
            conversation.AddAssistantMessage(instruction.Trim());
            return InstructionOutcome.Wait;
        }

        if (instruction.StartsWith("[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            var summary = instruction.Trim();
            conversation.AddAssistantMessage(summary);
            return InstructionOutcome.Done;
        }

        if (instruction.StartsWith("[CONFIRM]", StringComparison.OrdinalIgnoreCase))
        {
            var action = instruction.Substring("[CONFIRM]".Length).Trim();
            conversation.AddAssistantMessage(instruction.Trim());
            onStatus?.Invoke($"⚠ Action sensible : {action}");
            onSpoken?.Invoke($"J'ai besoin de votre confirmation : {action}");

            if (onConfirmationRequest is not null)
            {
                var approved = await onConfirmationRequest(action);
                if (approved)
                {
                    onStatus?.Invoke("✅ Confirmé par l'utilisateur — poursuite.");
                    conversation.AddUserMessage("(L'utilisateur a VALIDÉ cette action.) Poursuis.");
                }
                else
                {
                    onStatus?.Invoke("⛔ Refusé par l'utilisateur — on n'exécute pas l'action.");
                    conversation.AddUserMessage("(L'utilisateur a REFUSÉ cette action.) Explique poliment à l'interlocuteur que tu ne peux pas poursuivre sur ce point, puis conclus si nécessaire.");
                }
            }
            else
            {
                onStatus?.Invoke("Pas de canal de confirmation — on n'exécute PAS l'action sensible.");
                conversation.AddUserMessage("(Pas de confirmation possible ici.) N'exécute pas l'action sensible ; demande plutôt les infos nécessaires ou conclus proprement.");
            }
            return InstructionOutcome.Speak;
        }

        if (instruction.StartsWith("[SPEAK]", StringComparison.OrdinalIgnoreCase))
        {
            var text = instruction.Substring("[SPEAK]".Length).Trim().TrimStart(':').Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                onStatus?.Invoke("Balise [SPEAK] vide — on attend la réponse de l'interlocuteur.");
                conversation.AddAssistantMessage("[SPEAK] (vide)");
                return InstructionOutcome.Wait;
            }
            conversation.AddAssistantMessage($"[SPEAK] {text}");
            onSpoken?.Invoke($"Jarvis → {contact} : {text}");
            var send = await _driver.SendTextAsync(contact, text, ct);
            if (!send.Success)
            {
                onStatus?.Invoke($"Échec d'envoi : {send.Output}");
                _logger.LogWarning("[WhatsAppAgent] Envoi échoué: {Out}", send.Output);
                return InstructionOutcome.Abort;
            }
            return InstructionOutcome.Wait;
        }

        // Balise inconnue : on la devient comme du texte à prononcer (défensif).
        onStatus?.Invoke($"Réponse LLM non balisée, envoi direct : {TrimForLog(instruction)}");
        conversation.AddAssistantMessage(instruction.Trim());
        onSpoken?.Invoke($"Jarvis → {contact} : {instruction.Trim()}");
        await _driver.SendTextAsync(contact, instruction.Trim(), ct);
        return InstructionOutcome.Wait;
    }

    private static string ExtractDoneSummary(string instruction, string raw)
    {
        var idx = instruction.IndexOf("[DONE]", StringComparison.OrdinalIgnoreCase);
        var summary = instruction[(idx + "[DONE]".Length)..].Trim().TrimStart(':').Trim();
        return string.IsNullOrWhiteSpace(summary) ? raw.Trim() : summary;
    }

    private static string TrimForLog(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private Task SpeaksAway(string text) => Task.Run(async () =>
    {
        try { await _voice!.Value.SpeakAsync(text); }
        catch (Exception ex) { _logger.LogWarning(ex, "[WhatsAppAgent] Échec SpeakAsync"); }
    });
}
