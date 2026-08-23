namespace JarvisAI.Application.Presence;

public sealed class PresenceOptions
{
    /// <summary>IP du téléphone (réservation DHCP recommandée). Vide = désactivé.</summary>
    public string PhoneIp { get; set; } = string.Empty;

    /// <summary>Intervalle entre deux pings en secondes.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Durée d'absence consécutive (s) avant de déclencher « parti ».</summary>
    public int AbsenceThresholdSeconds { get; set; } = 600;

    /// <summary>Détection active au démarrage.</summary>
    public bool Enabled { get; set; } = false;
}
