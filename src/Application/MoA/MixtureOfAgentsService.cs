using JarvisAI.Application.Abstractions;
using JarvisAI.Application.AI;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.MoA;

/// <summary>
/// Mixture-of-Agents (MoA) : fait appel à plusieurs modèles de référence
/// pour obtenir des avis indépendants avant de formuler la réponse finale.
/// Améliore la qualité en croisant les perspectives de différents modèles.
/// </summary>
public sealed class MixtureOfAgentsService
{
    private readonly AIService _ai;
    private readonly ILogger<MixtureOfAgentsService> _logger;
    private readonly MoASettings _settings;

    public MixtureOfAgentsService(AIService ai, ILogger<MixtureOfAgentsService> logger, MoASettings? settings = null)
    {
        _ai = ai;
        _logger = logger;
        _settings = settings ?? new MoASettings();
    }

    /// <summary>
    /// Fait appel à N modèles de référence pour obtenir des conseils,
    /// puis retourne les réponses agrégées.
    /// </summary>
    public async Task<MoAResult> ConsultAsync(string prompt, string? primaryCategory = null, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || _settings.ReferenceModels.Count == 0)
            return new MoAResult(Array.Empty<MoAAdvisorResponse>(), prompt);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var advisors = new List<MoAAdvisorResponse>();

        // Sélectionner les modèles de référence (max advisorModels)
        var models = _settings.ReferenceModels.Take(_settings.MaxAdvisors).ToList();

        _logger.LogInformation("[MoA] Consultation de {Count} modèles de référence pour: {Prompt}",
            models.Count, prompt.Length > 80 ? prompt[..80] + "..." : prompt);

        // Lancer les consultations en parallèle
        var tasks = models.Select(async modelName =>
        {
            try
            {
                var response = await _ai.ChatAsync(prompt, model: modelName, cancellationToken: cancellationToken);
                return new MoAAdvisorResponse(modelName, response.Content, true, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MoA] Échec consultation modèle {Model}", modelName);
                return new MoAAdvisorResponse(modelName, null, false, ex.Message);
            }
        });

        var results = await Task.WhenAll(tasks);
        advisors.AddRange(results);

        sw.Stop();
        _logger.LogInformation("[MoA] Consultation terminée en {Elapsed}ms ({Success}/{Total} succès)",
            sw.ElapsedMilliseconds, advisors.Count(a => a.Success), advisors.Count);

        return new MoAResult(advisors, prompt);
    }

    /// <summary>
    /// Agrège les réponses des advisors en une réponse finale.
    /// Stratégie : prendre la réponse la plus fréquente ou la plus détaillée.
    /// </summary>
    public string Aggregate(IReadOnlyList<MoAAdvisorResponse> responses)
    {
        var successful = responses.Where(r => r.Success && !string.IsNullOrEmpty(r.Response)).ToList();
        if (successful.Count == 0)
            return "Aucun modèle de référence n'a pu répondre.";

        if (successful.Count == 1)
            return successful[0].Response!;

        // Stratégie simple : retourner la réponse la plus longue (souvent la plus détaillée)
        // On pourrait aussi utiliser un LLM pour résumer
        return successful.OrderByDescending(r => r.Response!.Length).First().Response!;
    }
}

public sealed class MoASettings
{
    public bool Enabled { get; set; } = false;
    public int MaxAdvisors { get; set; } = 3;
    public List<string> ReferenceModels { get; set; } = new();
}

public sealed class MoAResult
{
    public IReadOnlyList<MoAAdvisorResponse> Advisors { get; }
    public string OriginalPrompt { get; }

    public MoAResult(IReadOnlyList<MoAAdvisorResponse> advisors, string prompt)
    {
        Advisors = advisors;
        OriginalPrompt = prompt;
    }
}

public sealed record MoAAdvisorResponse(string Model, string? Response, bool Success, string? Error);
