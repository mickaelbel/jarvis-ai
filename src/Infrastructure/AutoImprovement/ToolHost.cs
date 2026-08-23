using JarvisAI.Application.AutoImprovement;
using JarvisAI.Application.Security;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace JarvisAI.Infrastructure.AutoImprovement;

/// <summary>
/// Implémentation des capabilities accordées au code C# auto-créé :
/// accès fichier limité au workspace (sauf mode autonome), HTTP public uniquement,
/// deadline stricte.
/// </summary>
public sealed class ToolHost : IToolHost
{
    private readonly ILogger<ToolHost> _logger;
    private readonly HttpClient _http;
    private readonly DateTime _deadline;
    private readonly StringBuilder _log = new();
    private readonly ISecurityManager? _security;

    private const int MaxResponseBytes = 2_000_000;

    public ToolHost(ILogger<ToolHost> logger, HttpClient http, TimeSpan timeout, ISecurityManager? security = null)
    {
        _logger = logger;
        _http = http;
        _deadline = DateTime.UtcNow.Add(timeout);
        _security = security;
    }

    public string NowUtc()
    {
        CheckDeadline();
        return DateTime.UtcNow.ToString("O");
    }

    public string ReadTextFile(string path)
    {
        CheckDeadline();
        if (!AllowFullFileAccess() && AutoToolGuardrails.IsPathProtected(path))
            throw new UnauthorizedAccessException("File access is restricted to the workspace.");
        return File.ReadAllText(Path.GetFullPath(path));
    }

    public string WriteTextFile(string path, string content)
    {
        CheckDeadline();
        if (!AllowFullFileAccess() && AutoToolGuardrails.IsPathProtected(path))
            throw new UnauthorizedAccessException("File access is restricted to the workspace.");
        if (content.Length > 2_000_000)
            throw new InvalidOperationException("Content too large.");
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
        return "ok";
    }

    public string HttpGet(string url)
    {
        CheckDeadline();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Only http(s) URLs are allowed.");

        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        if (!AllowFullFileAccess() && IsLoopback(host))
            throw new InvalidOperationException("Local/loopback URLs are blocked.");

        using var response = _http.GetAsync(uri).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            return $"HTTP {(int)response.StatusCode} {response.StatusCode}";

        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (bytes.Length > MaxResponseBytes)
            throw new InvalidOperationException("Response too large.");
        return Encoding.UTF8.GetString(bytes);
    }

    public string Log(string message)
    {
        CheckDeadline();
        _log.AppendLine(message);
        _logger.LogDebug("[AutoTool.Host] {Message}", message);
        return "";
    }

    private void CheckDeadline()
    {
        if (DateTime.UtcNow > _deadline)
            throw new TimeoutException("Auto-tool exceeded its time budget.");
    }

    /// <summary>
    /// En mode autonome, le code C# auto-créé accède à tout le disque et peut
    /// interroger les services locaux (loopback), comme le reste des outils.
    /// </summary>
    private bool AllowFullFileAccess()
        => _security is not null && _security.GetOptions().Mode == OperationMode.Autonomous;

    private static bool IsLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host.Split(':')[0], out var ip))
            return IPAddress.IsLoopback(ip);

        return false;
    }
}

public sealed class ToolHostFactory : IToolHostFactory
{
    private readonly ILogger<ToolHost> _logger;
    private readonly ISecurityManager? _security;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public ToolHostFactory(ILogger<ToolHost> logger, ISecurityManager? security = null)
    {
        _logger = logger;
        _security = security;
    }

    public IToolHost Create() => new ToolHost(_logger, Http, DefaultTimeout, _security);
}
