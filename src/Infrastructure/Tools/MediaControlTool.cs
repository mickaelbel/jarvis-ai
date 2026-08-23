using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using System.Runtime.InteropServices;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Contrôle média et volume système par touches virtuelles Windows (VK codes),
/// comme les touches multimédia d'un clavier — aucun driver requis.
/// </summary>
public sealed class MediaControlTool : ITool
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KeyUp = 0x0002;
    private const byte VkVolumeMute = 0xAD;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const byte VkMediaNext = 0xB0;
    private const byte VkMediaPrev = 0xB1;
    private const byte VkMediaStop = 0xB2;
    private const byte VkMediaPlayPause = 0xB3;

    public string Name => "media";
    public string Description =>
        "Contrôle du média en cours et du volume système (comme les touches multimédia). " +
        "Actions : play_pause, next, previous, stop, volume_up, volume_down, mute. " +
        "Pour « mets en pause la musique », « chanson suivante », « baisse le volume », « coupe le son ».";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public bool McpExpose => true;
    public string WaitingPhrase => "Je pilote le média.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "play_pause | next | previous | stop | volume_up | volume_down | mute", typeof(string), required: true)
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        var vk = (action?.ToLowerInvariant()) switch
        {
            "play_pause" or "play" or "pause" => VkMediaPlayPause,
            "next" => VkMediaNext,
            "previous" or "prev" => VkMediaPrev,
            "stop" => VkMediaStop,
            "volume_up" => VkVolumeUp,
            "volume_down" => VkVolumeDown,
            "mute" => VkVolumeMute,
            _ => (byte)0
        };
        if (vk == 0)
            return Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : play_pause, next, previous, stop, volume_up, volume_down, mute"));

        // Répète volume_up/down 3× pour un changement audible (~6 % par appui).
        var repeats = action is "volume_up" or "volume_down" ? 3u : 1u;
        for (uint i = 0; i < repeats; i++)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KeyUp, UIntPtr.Zero);
            if (repeats > 1) Thread.Sleep(30);
        }
        return Task.FromResult(ToolResult.Succeeded($"{action} exécuté. ACTION TERMINÉE."));
    }
}

