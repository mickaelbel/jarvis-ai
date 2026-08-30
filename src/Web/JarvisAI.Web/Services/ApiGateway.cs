using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IApiGateway
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    bool IsRunning { get; }
    int Port { get; }
}

public sealed class ApiGateway : IApiGateway, IDisposable
{
    private readonly ILogger<ApiGateway> _logger;
    private readonly IServiceProvider _services;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public bool IsRunning => _listener?.IsListening ?? false;
    public int Port { get; } = 17010;

    public ApiGateway(ILogger<ApiGateway> logger, IServiceProvider services)
    {
        _logger = logger;
        _services = services;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenerTask = ListenLoop(_cts.Token);

        _logger.LogInformation("[ApiGateway] Started on port {Port}", Port);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        _listener?.Stop();

        if (_listenerTask is not null)
            await _listenerTask;

        _logger.LogInformation("[ApiGateway] Stopped");
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequestAsync(context);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ApiGateway] Listener error");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;

            _logger.LogDebug("[ApiGateway] {Method} {Path}", method, path);

            object? result = path switch
            {
                "/api/status" => await HandleStatusAsync(),
                "/api/tools" => await HandleToolsAsync(),
                "/api/chat" when method == "POST" => await HandleChatAsync(request),
                _ => new { error = "Not found", path }
            };

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            var buffer = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.StatusCode = path.StartsWith("/api/") ? 200 : 404;
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ApiGateway] Request failed");
            response.StatusCode = 500;
        }
        finally
        {
            response.Close();
        }
    }

    private async Task<object> HandleStatusAsync()
    {
        return await Task.FromResult(new
        {
            status = "running",
            version = "2.0",
            port = Port,
            timestamp = DateTime.UtcNow
        });
    }

    private async Task<object> HandleToolsAsync()
    {
        return await Task.FromResult(new { tools = new[] { "terminal", "file_system", "browser", "computer_use", "memory" } });
    }

    private async Task<object> HandleChatAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(body);
        var message = data.GetProperty("message").GetString() ?? "";

        return await Task.FromResult(new
        {
            response = $"[ApiGateway] Received: {message}",
            timestamp = DateTime.UtcNow
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _listener?.Stop();
        _listener?.Close();
    }
}
