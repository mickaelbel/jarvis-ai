using JarvisAI.Application.AI;
using JarvisAI.Application.Memory;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Application.Agents;

public sealed class AutonomousAgentLoop : IAutonomousAgentLoop
{
    private readonly AIService _aiService;
    private readonly IAIProvider _provider;
    private readonly IObservationProvider _observationProvider;
    private readonly IMemoryService _memoryService;
    private readonly AutonomousLoopOptions _options;
    private readonly ILogger<AutonomousAgentLoop> _logger;

    private const string SystemPrompt =
        "You are Jarvis, an autonomous assistant that operates the user's computer and web browser. " +
        "To accomplish the GOAL you receive observations of the current screen/page state. " +
        "Use the available tools to act (navigate, click, type, run commands, read files...). " +
        "Do NOT claim the goal is done unless the observations confirm it. " +
        "When the goal is genuinely complete, reply with a concise final answer in the user's language. " +
        "If a correction was given, apply it in your next action.";

    private const string VerificationSystemPrompt =
        "You are a strict verification agent. Given a GOAL, the LATEST ACTION OUTPUT of an autonomous " +
        "assistant, and an OBSERVATION of the current state, decide whether the goal is fully accomplished. " +
        "Reply ONLY with a single JSON object (no markdown, no extra text) in this exact shape: " +
        "{\"verified\": true, \"reason\": \"short reason\", \"correction\": \"concrete next action if not verified, else empty string\"}";

    public AutonomousAgentLoop(
        AIService aiService,
        IAIProvider provider,
        IObservationProvider observationProvider,
        IMemoryService memoryService,
        AutonomousLoopOptions options,
        ILogger<AutonomousAgentLoop> logger)
    {
        _aiService = aiService;
        _provider = provider;
        _observationProvider = observationProvider;
        _memoryService = memoryService;
        _options = options;
        _logger = logger;
    }

    public async Task<AutonomousLoopResult> ExecuteAsync(string goal, CancellationToken cancellationToken = default)
    {
        goal = goal?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(goal))
            return new AutonomousLoopResult(false, string.Empty, 0, 0, "Empty goal");

        var conversation = new AIConversation(SystemPrompt);
        var corrections = new List<string>();
        var consecutiveErrors = 0;

        for (var iteration = 1; iteration <= _options.MaxIterations; iteration++)
        {
            _logger.LogInformation("[AutonomousAgentLoop] Iteration {Iteration}/{Max} for goal: {Goal}", iteration, _options.MaxIterations, goal);

            var observation = await ObserveAsync(goal, cancellationToken);
            var instruction = BuildIterationMessage(goal, iteration, observation, corrections.LastOrDefault());

            AIResponse action;
            try
            {
                action = await _aiService.ChatAsync(instruction, conversation, model: null, cancellationToken);
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _logger.LogError(ex, "[AutonomousAgentLoop] Action error on iteration {Iteration}", iteration);
                if (consecutiveErrors >= _options.MaxConsecutiveErrors)
                    return new AutonomousLoopResult(false, string.Empty, iteration, corrections.Count, "Repeated action errors");
                continue;
            }

            if (!action.Success)
            {
                consecutiveErrors++;
                _logger.LogWarning("[AutonomousAgentLoop] Action failed on iteration {Iteration}: {Error}", iteration, action.ErrorMessage);
                if (consecutiveErrors >= _options.MaxConsecutiveErrors)
                    return new AutonomousLoopResult(
                        false, action.Content, iteration, corrections.Count,
                        string.IsNullOrWhiteSpace(action.ErrorMessage) ? "Repeated action errors" : $"Repeated action errors: {action.ErrorMessage}");
                continue;
            }
            consecutiveErrors = 0;

            var verdict = await VerifyAsync(goal, action.Content, observation, cancellationToken);

            if (verdict.Verified)
            {
                _logger.LogInformation("[AutonomousAgentLoop] Goal verified on iteration {Iteration}", iteration);
                await SaveOutcomeAsync(goal, action.Content, true, cancellationToken);
                return new AutonomousLoopResult(true, action.Content, iteration, corrections.Count, null);
            }

            corrections.Add(verdict.Correction ?? string.Empty);
            _logger.LogWarning("[AutonomousAgentLoop] Iteration {Iteration} not verified: {Reason}", iteration, verdict.Reason);

            if (string.IsNullOrWhiteSpace(verdict.Correction))
            {
                await SaveOutcomeAsync(goal, action.Content, false, cancellationToken);
                return new AutonomousLoopResult(
                    false, action.Content, iteration, corrections.Count,
                    $"Goal not verified and no correction available ({verdict.Reason ?? "unknown"})");
            }
        }

        return new AutonomousLoopResult(
            false, "Maximum iterations reached without verification", _options.MaxIterations, corrections.Count,
            $"Maximum iterations ({_options.MaxIterations}) reached");
    }

    private async Task<string> ObserveAsync(string goal, CancellationToken ct)
    {
        try
        {
            return await _observationProvider.ObserveAsync(goal, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutonomousAgentLoop] Observation failed");
            return string.Empty;
        }
    }

    private static string BuildIterationMessage(string goal, int iteration, string observation, string? correction)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"GOAL: {goal}");
        builder.AppendLine($"ITERATION: {iteration}");
        builder.AppendLine(observation.Length > 0
            ? $"OBSERVATION:\n{observation}"
            : "OBSERVATION: (no live observation available)");
        builder.AppendLine(string.IsNullOrWhiteSpace(correction)
            ? "CORRECTION: none (first pass)"
            : $"CORRECTION TO APPLY:\n{correction}");
        builder.Append("Proceed: act with a tool call, or reply with the final answer if the goal is fully achieved.");
        return builder.ToString();
    }

    private async Task<VerificationVerdict> VerifyAsync(string goal, string actionOutput, string observation, CancellationToken ct)
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
                model: _options.VerificationModel,
                temperature: 0.0f,
                maxTokens: 512);

            var response = await _provider.ChatAsync(request, ct);
            if (!response.Success)
                return new VerificationVerdict(false, "Verification provider error", null);

            return ParseVerdict(response.Content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutonomousAgentLoop] Verification call failed");
            return new VerificationVerdict(false, "Verification call failed", null);
        }
    }

    internal static VerificationVerdict ParseVerdict(string content)
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
            content = System.Text.RegularExpressions.Regex.Replace(content, @"```(json)?", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

    private async Task SaveOutcomeAsync(string goal, string outcome, bool success, CancellationToken ct)
    {
        try
        {
            var key = $"task_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
            await _memoryService.SaveAsync(
                key,
                $"Goal: {goal}\nResult: {(success ? "SUCCESS" : "PARTIAL")}\n{outcome}",
                MemoryType.Fact,
                "agent_task",
                importance: success ? 0.7f : 0.4f,
                ttl: TimeSpan.FromDays(7),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutonomousAgentLoop] Failed to save task outcome");
        }
    }

    internal sealed record VerificationVerdict(bool Verified, string? Reason, string? Correction);
}
