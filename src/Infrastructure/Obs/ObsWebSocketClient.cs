using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Obs;

public sealed class ObsOptions
{
    public bool Enabled { get; set; } = false;
    /// <summary>URL du serveur obs-websocket v5 (OBS : Outils → WebSocket Server Settings).</summary>
    public string Url { get; set; } = "ws://127.0.0.1:4455";
    public string Password { get; set; } = "";
}

/// <summary>
/// Client minimal obs-websocket v5 (protocole JSON sur WebSocket, avec
/// challenge/salt SHA-256) : scènes, stream, enregistrement, replay buffer.
/// </summary>
public sealed class ObsWebSocketClient : IDisposable
{
    private readonly ObsOptions _options;
    private System.Net.WebSockets.ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _requestId;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    public ObsWebSocketClient(ObsOptions options) => _options = options;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_ws?.State == System.Net.WebSockets.WebSocketState.Open) return;
        _ws = new System.Net.WebSockets.ClientWebSocket();
        await _ws.ConnectAsync(new Uri(_options.Url), ct);
        var hello = await ReceiveJsonAsync(ct);

        // Auth obligatoire si le pont exige un mot de passe.
        if (hello.TryGetProperty("d", out var d) && d.TryGetProperty("authentication", out var auth))
        {
            var salt = auth.GetProperty("salt").GetString() ?? "";
            var challenge = auth.GetProperty("challenge").GetString() ?? "";
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(_options.Password + salt)));
            var authToken = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
            await SendRequestAsync(1, "Identify", new { rpcVersion = 1, authentication = authToken }, null, ct);
            await ReceiveJsonAsync(ct); // Identified
        }
        else
        {
            await SendRequestAsync(1, "Identify", new { rpcVersion = 1 }, null, ct);
            await ReceiveJsonAsync(ct);
        }
        _ = Task.Run(() => ListenLoopAsync(CancellationToken.None));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (_ws?.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var msg = await ReceiveJsonAsync(ct);
                if (msg.TryGetProperty("op", out var op) && op.GetInt32() == 7 &&
                    msg.TryGetProperty("d", out var resp))
                {
                    var rid = resp.TryGetProperty("requestId", out var r) ? r.GetString() : null;
                    if (rid is not null && _pending.Remove(rid, out var tcs))
                    {
                        var ok = !resp.TryGetProperty("requestStatus", out var st) || !st.TryGetProperty("code", out var code) || code.GetInt32() >= 200;
                        tcs.TrySetResult(ok ? resp : throw new InvalidOperationException(
                            resp.TryGetProperty("requestStatus", out var st2) ? st2.GetProperty("comment").GetString() ?? "requête refusée" : "requête refusée"));
                    }
                }
            }
            catch { break; }
        }
    }

    public async Task<JsonElement> CallAsync(string requestType, object? requestData = null, CancellationToken ct = default)
    {
        if (_ws is null || _ws.State != System.Net.WebSockets.WebSocketState.Open)
            await ConnectAsync(ct);
        var rid = $"r{Interlocked.Increment(ref _requestId)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[rid] = tcs;
        await SendRequestAsync(6, requestType, requestData, rid, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.Remove(rid);
            throw new TimeoutException($"OBS n'a pas répondu à « {requestType} ».");
        }
    }

    private async Task SendRequestAsync(int opCode, string type, object? data, string? requestId, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            op = opCode,
            d = requestId is null
                ? new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 }
                : (object)new Dictionary<string, object?>
                {
                    ["requestType"] = type,
                    ["requestData"] = data,
                    ["requestId"] = requestId
                }
        });
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws!.SendAsync(payload, System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
        }
        finally { _sendLock.Release(); }
    }

    private async Task<JsonElement> ReceiveJsonAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var segment = new ArraySegment<byte>(buffer);
            var result = await _ws!.ReceiveAsync(segment, ct);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) break;
        }
        return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
    }

    public void Dispose()
    {
        try { _ws?.Dispose(); } catch { }
    }
}
