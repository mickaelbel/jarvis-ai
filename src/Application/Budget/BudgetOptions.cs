namespace JarvisAI.Application.Budget;

public sealed class BudgetOptions
{
    /// <summary>Plafond journalier en dollars (null = illimité).</summary>
    public decimal? DailyCapUsd { get; set; }

    /// <summary>Plafond mensuel en dollars (null = illimité).</summary>
    public decimal? MonthlyCapUsd { get; set; }

    /// <summary>Pourcentage du plafond déclenchant une alerte vocale.</summary>
    public int AlertPercent { get; set; } = 80;

    /// <summary>Modèle local de repli quand le plafond est dépassé (bascule auto).</summary>
    public string LocalFallbackModel { get; set; } = "llama3.3:latest";

    /// <summary>Bascule automatique vers le modèle local au plafond.</summary>
    public bool AutoSwitchToLocalAtCap { get; set; } = true;

    /// <summary>
    /// Prix par million de tokens, matché par plus longue sous-chaîne du nom du modèle.
    /// Clé → (input $/Mtok, output $/Mtok).
    /// </summary>
    public Dictionary<string, (decimal InputPerMillion, decimal OutputPerMillion)> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["haiku"] = (1.0m, 5.0m),
        ["sonnet"] = (3.0m, 15.0m),
        ["opus"] = (5.0m, 25.0m),
        ["gpt-4o-mini"] = (0.15m, 0.60m),
        ["gpt-4o"] = (2.5m, 10.0m),
        ["gpt-5-mini"] = (0.25m, 2.0m),
        ["deepseek"] = (0.27m, 1.1m),
        // Ollama / modèles locaux = gratuit : aucune entrée nécessaire.
    };
}
