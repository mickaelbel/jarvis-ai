namespace JarvisAI.Application.Services;

/// <summary>
/// Agent téléphonique générique réutilisable par tous les agents/outils de Jarvis.
/// Il pilote WhatsApp Web (messages + appel vocal best-effort) pour mener une
/// conversation avec un contact vers un objectif, de façon autonome.
/// </summary>
public interface IWhatsAppPhoneAgent
{
    /// <summary>Nom du canal (ex: "WhatsApp").</summary>
    string ChannelName { get; }

    /// <summary>
    /// Vérifie que le driver est disponible (WhatsApp Web joignable).
    /// WhatsApp Web n'étant pas API-able, on se contente d'indiquer que le
    /// driver est présent ; le login QR se fait à la première utilisation.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Envoie un message texte au contact (numéro ou nom affiché WhatsApp).</summary>
    Task<WhatsAppResult> SendTextAsync(string contact, string message, CancellationToken ct = default);

    /// <summary>
    /// Ouvre la conversation avec le contact et renvoie les derniers messages
    /// (identifiés par auteur + texte + horodatage), pour que l'agent puisse
    /// comprendre la réponse de l'interlocuteur.
    /// </summary>
    Task<WhatsAppResult> ReadConversationAsync(string contact, int maxMessages = 10, CancellationToken ct = default);

    /// <summary>
    /// Attend qu'une nouvelle réponse (pas de l'agent) arrive dans la
    /// conversation ouverte, jusqu'à <paramref name="timeout"/>. Renvoie le
    /// message reçu, ou un timeout.
    /// </summary>
    Task<WhatsAppResult> WaitForReplyAsync(string contact, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Appel vocal best-effort : clique sur le bouton d'appel de la conversation.
    /// WhatsApp Web n'expose pas d'API audio exploitable ; l'appel est plaçé mais
    /// la voix passe par le micro/haut-parleur du PC (pas de flux STT/TTS fiable).
    /// </summary>
    Task<WhatsAppResult> PlaceVoiceCallAsync(string contact, CancellationToken ct = default);

    /// <summary>Raccroche un appel vocal en cours (best-effort).</summary>
    Task<WhatsAppResult> HangUpCallAsync(CancellationToken ct = default);

    /// <summary>Vérifie si une conversation avec le contact est joignable.</summary>
    Task<WhatsAppResult> EnsureContactAsync(string contact, CancellationToken ct = default);

    /// <summary>
    /// Mène une conversation téléphonique autonome vers un objectif, en langage
    /// naturel, avec le contact. L'agent se présente, pose les questions, attend
    /// les réponses, demande confirmation (via <paramref name="onConfirmationRequest"/>)
    /// avant toute action sensible, et fournit un résumé en fin d'appel.
    /// </summary>
    /// <param name="goal">Objectif (ex: « appelle le restaurant pour réserver une table à 20h pour 4 »).</param>
    /// <param name="contact">Contact WhatsApp (nom ou numéro).</param>
    /// <param name="onSpoken">Callback de sortie vocale/affichage du texte prononcé (TTS).</param>
    /// <param name="onStatus">Callback de statut intermédiaire (surveillance).</param>
    /// <param name="onConfirmationRequest">Callback : demande une décision à l'utilisateur pour une action sensible.</param>
    /// <param name="maxTurns">Nombre maximal de tours de conversation.</param>
    /// <param name="ct">Annulation.</param>
    Task<WhatsAppConversationReport> RunAutonomousConversationAsync(
        string goal,
        string contact,
        Action<string>? onSpoken = null,
        Action<string>? onStatus = null,
        Func<string, Task<bool>>? onConfirmationRequest = null,
        int maxTurns = 12,
        CancellationToken ct = default);
}

/// <summary>Compte-rendu d'une conversation téléphonique autonome.</summary>
public sealed record WhatsAppConversationReport(
    bool Success,
    string Transcript,
    string Summary,
    string? ErrorMessage = null);

public sealed record WhatsAppResult(bool Success, string Output, string? ErrorMessage = null)
{
    public static WhatsAppResult Ok(string output) => new(true, output);
    public static WhatsAppResult Fail(string error) => new(false, string.Empty, error);
}
