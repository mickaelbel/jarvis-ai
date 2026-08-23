using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace JarvisAI.Hue;

public sealed class HueOptions
{
    /// <summary>IP du pont Hue. Vide = découverte SSDP automatique.</summary>
    public string BridgeIp { get; set; } = "";
    /// <summary>Nom d'utilisateur (app key) obtenu lors de l'appairage.</summary>
    public string AppKey { get; set; } = "";
}

/// <summary>
/// Client minimal de l'API REST locale du pont Philips Hue (CLIP v1) :
/// découverte SSDP, appairage (bouton du pont), lumières, groupes, scènes.
/// </summary>
public sealed class HueBridgeClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly HueOptions _options;
    private string? _resolvedIp;

    public HueBridgeClient(HueOptions options) => _options = options;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.AppKey);

    public async Task<string?> DiscoverAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_options.BridgeIp)) return _options.BridgeIp;
        if (_resolvedIp is not null) return _resolvedIp;

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = 3000;
        var msearch = System.Text.Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: SsdpSearch:all\r\n\r\n");
        await udp.SendAsync(msearch, msearch.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var res = await udp.ReceiveAsync(ct);
                var desc = System.Text.Encoding.ASCII.GetString(res.Buffer);
                if (!desc.Contains("IpBridge", StringComparison.OrdinalIgnoreCase) &&
                    !desc.Contains("hue", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var line in desc.Split('\n'))
                {
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    {
                        var url = line["LOCATION:".Length..].Trim();
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                            return _resolvedIp = uri.Host;
                    }
                }
            }
            catch { /* timeout d'attente : on retente jusqu'au deadline */ }
        }
        return null;
    }

    /// <summary>Appairage : appuie sur le bouton du pont puis appelle ceci.</summary>
    public async Task<string> PairAsync(string? ip, CancellationToken ct = default)
    {
        var host = ip ?? await DiscoverAsync(ct) ?? throw new InvalidOperationException("Pont Hue introuvable sur le réseau.");
        var body = """{"devicetype":"jarvis#pc","generateclientkey":true}""";
        var resp = await _http.PostAsync($"http://{host}/api", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("success", out var success))
            return success.GetProperty("username").GetString() ?? "";
        throw new InvalidOperationException(
            json.Contains("link button", StringComparison.OrdinalIgnoreCase)
                ? "Appuie sur le bouton du pont Hue puis redemande l'appairage."
                : $"Appairage refusé : {json}");
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        var ip = await DiscoverAsync(ct) ?? throw new InvalidOperationException("Pont Hue introuvable.");
        var resp = await _http.GetAsync($"http://{ip}/api/{_options.AppKey}{path}", ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private async Task<JsonElement> PutJsonAsync(string path, object body, CancellationToken ct)
    {
        var ip = await DiscoverAsync(ct) ?? throw new InvalidOperationException("Pont Hue introuvable.");
        var resp = await _http.PutAsync($"http://{ip}/api/{_options.AppKey}{path}",
            new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return text.Contains("error", StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException($"Le pont a refusé : {text}")
            : JsonDocument.Parse("{}").RootElement;
    }

    /// <summary>Liste les lumières avec état et nom.</summary>
    public async Task<IReadOnlyList<(string Id, string Name, bool On, byte Bri)>> ListLightsAsync(CancellationToken ct = default)
    {
        var root = await GetJsonAsync("/lights", ct);
        var list = new List<(string, string, bool, byte)>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object || !prop.Value.TryGetProperty("state", out var state))
                continue;
            var name = prop.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var on = state.TryGetProperty("on", out var o) && o.GetBoolean();
            var bri = state.TryGetProperty("bri", out var b) ? (byte)Math.Min(byte.MaxValue, b.GetInt32()) : (byte)0;
            list.Add((prop.Name, name, on, bri));
        }
        return list;
    }

    public Task SetLightAsync(string lightId, bool? on, int? briPercent, string? colorName, CancellationToken ct = default)
        => SetTargetAsync($"/lights/{lightId}/state", on, briPercent, colorName, ct);

    public Task SetGroupAsync(string groupId, bool? on, int? briPercent, string? colorName, CancellationToken ct = default)
        => SetTargetAsync($"/groups/{groupId}/action", on, briPercent, colorName, ct);

    private static readonly Dictionary<string, (double X, double Y)> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rouge"] = (0.675, 0.322), ["red"] = (0.675, 0.322),
        ["vert"] = (0.4091, 0.518), ["green"] = (0.4091, 0.518),
        ["bleu"] = (0.167, 0.04), ["blue"] = (0.167, 0.04),
        ["blanc"] = (0.3227, 0.329), ["white"] = (0.3227, 0.329),
        ["orange"] = (0.5005, 0.4182),
        ["jaune"] = (0.4208, 0.504), ["yellow"] = (0.4208, 0.504),
        ["rose"] = (0.4072, 0.2818), ["pink"] = (0.4072, 0.2818),
        ["violet"] = (0.2695, 0.1233), ["purple"] = (0.2695, 0.1233)
    };

    private async Task SetTargetAsync(string path, bool? on, int? briPercent, string? colorName, CancellationToken ct)
    {
        var body = new Dictionary<string, object>();
        if (on is not null) body["on"] = on.Value;
        if (briPercent is not null)
            body["bri"] = Math.Clamp(briPercent.Value, 1, 100) * 254 / 100;
        if (!string.IsNullOrWhiteSpace(colorName) &&
            NamedColors.TryGetValue(colorName.Trim(), out var xy))
            body["xy"] = new[] { Math.Round(xy.X, 4), Math.Round(xy.Y, 4) };
        await PutJsonAsync(path, body, ct);
    }

    public async Task ActivateSceneAsync(string sceneId, string groupId, CancellationToken ct = default)
        => await PutJsonAsync($"/groups/{groupId}/action", new { scene = sceneId }, ct);

    public async Task<IReadOnlyList<(string Id, string Name)>> ListScenesAsync(CancellationToken ct = default)
    {
        var root = await GetJsonAsync("/scenes", ct);
        var list = new List<(string, string)>();
        foreach (var prop in root.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("name", out var n))
                list.Add((prop.Name, n.GetString() ?? ""));
        return list;
    }

    public async Task<IReadOnlyList<(string Id, string Name, bool AnyOn)>> ListGroupsAsync(CancellationToken ct = default)
    {
        var root = await GetJsonAsync("/groups", ct);
        var list = new List<(string, string, bool)>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var name = prop.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var anyOn = prop.Value.TryGetProperty("state", out var s) &&
                        s.TryGetProperty("any_on", out var ao) && ao.GetBoolean();
            list.Add((prop.Name, name, anyOn));
        }
        return list;
    }
}
