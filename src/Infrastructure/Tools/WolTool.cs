using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using System.Net;
using System.Net.Sockets;

namespace JarvisAI.Infrastructure.Tools;

public sealed class WolOptions
{
    /// <summary>MAC du PC à réveiller (format 00:1A:2B:3C:4D:5E), vide = désactivé.</summary>
    public string TargetMac { get; set; } = "";
}

/// <summary>
/// Wake-on-LAN : magic packet UDP (6×FF puis 16× la MAC) en broadcast.
/// Portage de docs/wol.md — réveille le PC via une prise connectée ou WOL.
/// </summary>
public sealed class WolTool : ITool
{
    private readonly WolOptions _options;

    public string Name => "wol";
    public string Description =>
        "Réveille un PC éteint par Wake-on-LAN (magic packet). Action : wake. " +
        "Utilise la MAC configurée par défaut, ou une MAC passée en paramètre.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "wake", typeof(string), required: true),
        new ToolParameter("mac", "Adresse MAC cible, optionnelle (sinon celle configurée)", typeof(string))
    };

    public WolTool(WolOptions options) => _options = options;

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        if (!string.Equals(action, "wake", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Failed("Action inconnue. Valide : wake"));
        parameters.TryGetValue("mac", out var mac);
        var target = string.IsNullOrWhiteSpace(mac) ? _options.TargetMac : mac;
        if (string.IsNullOrWhiteSpace(target))
            return Task.FromResult(ToolResult.Failed("Aucune MAC configurée pour Wake-on-LAN. Renseigne JarvisAI:Wol:TargetMac dans appsettings.json."));

        var cleanMac = target.Replace(":", "").Replace("-", "");
        if (cleanMac.Length != 12 || !System.Text.RegularExpressions.Regex.IsMatch(cleanMac, @"^[0-9a-fA-F]{12}$"))
            return Task.FromResult(ToolResult.Failed($"MAC invalide : « {target} » (format attendu 00:1A:2B:3C:4D:5E)."));

        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
            for (var j = 0; j < 6; j++)
                packet[6 + i * 6 + j] = Convert.ToByte(cleanMac.Substring(j * 2, 2), 16);

        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            foreach (var broadcast in new[] { IPAddress.Broadcast, IPAddress.Parse("255.255.255.255") })
                udp.Send(packet, packet.Length, new IPEndPoint(broadcast, 9));
            return Task.FromResult(ToolResult.Succeeded($"Magic packet envoyé à {target}. Le PC met quelques dizaines de secondes à démarrer. ACTION TERMINÉE."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed($"Échec de l'envoi Wake-on-LAN : {ex.Message}"));
        }
    }
}

