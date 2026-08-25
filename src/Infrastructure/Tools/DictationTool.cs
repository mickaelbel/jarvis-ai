using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Outil <c>dictee</c> : active/désactive le mode dictée.
/// Quand activé, tout ce que tu dis est tapé directement dans l'app
/// au premier plan (Word, navigation…) au lieu d'être envoyé au LLM.
/// Parle normalement, Jarvis transcrit en texte et le colle via Ctrl+V.
/// </summary>
public sealed class DictationTool : ITool
{
    private readonly IDictationService _service;
    public string Name => "dictee";
    public string Description => "Active ou désactive le mode dictée. Quand activé, tes paroles sont tapées en texte dans l'app au premier plan (clipboard + Ctrl+V) au lieu d'être analysées par le LLM. Actions : activer, desactiver, statut.";
    public string Category => "voice";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public bool McpExpose => true;
    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "activer | desactiver | statut", typeof(string), required: true)
    };

    public DictationTool(IDictationService service) => _service = service;

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var action = parameters.TryGetValue("action", out var a) ? a.Trim().ToLowerInvariant() : "";
        return Task.FromResult(action switch
        {
            "activer" or "active" or "on" => Activate(),
            "desactiver" or "desactive" or "off" or "stop" => Deactivate(),
            "statut" or "status" => ToolResult.Succeeded(_service.IsEnabled ? "Le mode dictée est ACTIVÉ. Les paroles sont tapées en texte." : "Le mode dictée est désactivé. Les paroles passent par l'IA."),
            _ => ToolResult.Failed("Action inconnue : activer, desactiver ou statut.")
        });
    }

    private ToolResult Activate()
    {
        _service.Activate();
        return ToolResult.Succeeded(
            "Mode dictée ACTIVÉ. Parle et ton texte sera tapé dans l'app au premier plan. " +
            "Pour arrêter, dis « Jarvis, arrête la dictée ».");
    }

    private ToolResult Deactivate()
    {
        _service.Deactivate();
        return ToolResult.Succeeded("Mode dictée désactivé. Je suis de retour en mode normal.");
    }
}
