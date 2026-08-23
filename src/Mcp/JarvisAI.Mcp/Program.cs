// Serveur MCP (stdio) pour JarvisAI — pont JSON-RPC 2.0 vers l'API locale
// http://127.0.0.1:51844/api/mcp/* (outils domotique/PC exposés par Jarvis).
// Usage dans Claude Desktop / Hermes :
//   { "command": "dotnet", "args": ["<chemin>/JarvisAI.Mcp.dll"] }
using System.Text;
using System.Text.Json;

var baseUrl = Environment.GetEnvironmentVariable("JARVIS_MCP_URL") ?? "http://127.0.0.1:51844";
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };

while (Console.In.Peek() >= 0)
{
    var line = await Console.In.ReadLineAsync();
    if (string.IsNullOrWhiteSpace(line)) continue;

    JsonDocument requestDoc;
    try { requestDoc = JsonDocument.Parse(line); }
    catch { continue; }

    using (requestDoc)
    {
        var root = requestDoc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : (JsonElement?)null;
        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
        object? result = method switch
        {
            "initialize" => new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "jarvis-ai", version = "1.0.0" }
            },
            "tools/list" => await ToolsListAsync(),
            "tools/call" => await ToolsCallAsync(root),
            "ping" => (object)new { },
            _ => null
        };

        var response = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result ?? new { }
        };
        if (method is not ("initialize" or "tools/list" or "tools/call" or "ping") && id is not null)
        {
            response.Remove("result");
            response["error"] = new { code = -32601, message = $"Méthode inconnue : {method}" };
        }

        Console.WriteLine(JsonSerializer.Serialize(response));
        await Console.Out.FlushAsync();
    }
}

static string Clean(string s) => s.Replace("\r\n", "\n").Trim();

async Task<object?> ToolsListAsync()
{
    try
    {
        var json = await http.GetStringAsync("/api/mcp/tools");
        using var doc = JsonDocument.Parse(json);
        var tools = doc.RootElement.EnumerateArray().Select(t => new
        {
            name = t.GetProperty("name").GetString(),
            description = t.GetProperty("description").GetString(),
            inputSchema = new
            {
                type = "object",
                properties = t.GetProperty("parameters").EnumerateArray().ToDictionary(
                    p => p.GetProperty("name").GetString()!,
                    p => (object)new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["description"] = p.GetProperty("description").GetString()
                    }),
                required = t.GetProperty("parameters").EnumerateArray()
                    .Where(p => p.GetProperty("required").GetBoolean())
                    .Select(p => p.GetProperty("name").GetString())
                    .ToList()
            }
        }).ToList();
        return new { tools };
    }
    catch (Exception ex)
    {
        return new { tools = Array.Empty<object>(), error = Clean(ex.Message) };
    }
}

async Task<object?> ToolsCallAsync(JsonElement root)
{
    try
    {
        var p = root.GetProperty("params");
        var name = p.GetProperty("name").GetString();
        var args = new Dictionary<string, string>();
        if (p.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object)
            foreach (var kv in arguments.EnumerateObject())
                args[kv.Name] = kv.Value.ToString();

        var payload = JsonSerializer.Serialize(new { name, args });
        var resp = await http.PostAsync("/api/mcp/call",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var output = doc.RootElement.TryGetProperty("output", out var o) ? o.GetString()
                     : doc.RootElement.TryGetProperty("message", out var msg) ? msg.GetString()
                     : doc.RootElement.TryGetProperty("error", out var err) ? err.GetString()
                     : "(vide)";
        return new
        {
            content = new[]
            {
                new { type = "text", text = output ?? "" }
            }
        };
    }
    catch (Exception ex)
    {
        return new { content = new[] { new { type = "text", text = "Erreur : " + Clean(ex.Message) } } };
    }
}
