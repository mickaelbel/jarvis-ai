namespace JarvisAI.Application.Budget;

public sealed record BudgetState(
    decimal TotalDayUsd,
    decimal TotalMonthUsd,
    decimal? DailyCap,
    decimal? MonthlyCap,
    bool AlertDayTriggered,
    bool AlertMonthTriggered,
    bool CapExceeded)
{
    public decimal? PercentOfDay => DailyCap is > 0 ? TotalDayUsd / DailyCap * 100 : null;
    public decimal? PercentOfMonth => MonthlyCap is > 0 ? TotalMonthUsd / MonthlyCap * 100 : null;
}

public interface IBudgetTracker
{
    /// <summary>Enregistre une consommation LLM et calcule le coût selon la table de prix.</summary>
    Task RecordAsync(string model, int promptTokens, int completionTokens, CancellationToken cancellationToken = default);

    Task<BudgetState> GetStateAsync(CancellationToken cancellationToken = default);

    string FormatSummary();

    /// <summary>Modèle à utiliser (repli local si plafond dépassé et bascule activée).</summary>
    string ApplyFallback(string requestedModel);
}
