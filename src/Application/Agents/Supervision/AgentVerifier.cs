using JarvisAI.Application.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Application.Agents.Supervision;

/// <summary>Verdict de vérification d'une action d'agent.</summary>
public sealed record VerificationVerdict(bool Verified, string? Reason, string? Correction);

/// <summary>
/// Vérificateur UNIFIÉ : décide si un objectif est réellement accompli en
/// croisant l'objectif, la sortie de la dernière action et une observation de
/// l'état courant. Ne jamais considérer une action comme réussie sans vérification.
/// </summary>
public interface IAgentVerifier
{
    Task<VerificationVerdict> VerifyAsync(
        string goal,
        string actionOutput,
        string observation,
        string? model = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implémentation par appel modèle (verdict JSON {verified, reason, correction}).
/// Factorise la logique qui était dupliquée entre AutonomousAgentLoop et
/// AIServiceAdapter. Utilise uniquement IAIProvider (abstraction modèle).
/// </summary>
public sealed class AgentVerifier : IAgentVerifier
{
    private readonly IAIProvider _provider;
    private readonly ILogger<AgentVerifier> _logger;

    public AgentVerifier(IAIProvider provider, ILogger<AgentVerifier>? logger = null)
    {
        _provider = provider;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentVerifier>.Instance;
    }

    public async Task<VerificationVerdict> VerifyAsync(
        string goal,
        string actionOutput,
        string observation,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = new[]
            {
                AIMessage.User(
                    $"GOAL:\n{goal}\n\nLATEST ACTION OUTPUT:\n{Truncate(actionOutput, 4000)}\n\nOBSERVATION:\n{Truncate(observation, 2000)}")
            };
            var request = new AIRequest(
                systemPrompt: VerificationSystemPrompt,
                messages: messages,
                tools: Array.Empty<AIToolDefinition>(),
                model: model,
                temperature: 0.0f,
                maxTokens: 512);

            var response = await _provider.ChatAsync(request, cancellationToken);
            if (!response.Success)
                return new VerificationVerdict(false, "Verification provider error", null);

            return ParseVerdict(response.Content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentVerifier] Verifier call failed");
            return new VerificationVerdict(false, "Verifier call failed", null);
        }
    }

    public static VerificationVerdict ParseVerdict(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new VerificationVerdict(false, "Empty verification response", null);

        var json = ExtractJson(content);
        if (json is null)
            return new VerificationVerdict(false, "Verification response unparseable", null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new VerificationVerdict(false, "Verification response not an object", null);

            var verified = root.TryGetProperty("verified", out var v)
                           && v.ValueKind == JsonValueKind.True;

            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var correction = root.TryGetProperty("correction", out var c) ? c.GetString() : null;

            return new VerificationVerdict(verified, reason, string.IsNullOrWhiteSpace(correction) ? null : correction);
        }
        catch (Exception)
        {
            return new VerificationVerdict(false, "Verification response invalid JSON", null);
        }
    }

    internal static string? ExtractJson(string content)
    {
        if (content.Contains("```"))
            content = System.Text.RegularExpressions.Regex.Replace(
                content, @"```(json)?", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return content[start..(end + 1)];
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= maxLength ? value
            : value[..maxLength] + "...";

    private const string VerificationSystemPrompt =
        "You are a strict verification agent. Given a GOAL, the LATEST ACTION OUTPUT of an autonomous " +
        "assistant, and an OBSERVATION of the current state, decide whether the goal is fully accomplished. " +
        "Reply ONLY with a single JSON object (no markdown, no extra text) in this exact shape: " +
        "{\"verified\": true, \"reason\": \"short reason\", \"correction\": \"concrete next action if not verified, else empty string\"}";
}
