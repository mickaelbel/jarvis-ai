using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ClipboardTool : ITool
{
    private readonly ILogger<ClipboardTool> _logger;
    private static readonly List<(string Text, DateTime Time)> _history = new();
    private static readonly object _historyLock = new();

    public string Name => "clipboard";
    public string Description => "Lis et écrit le presse-papier Windows avec historique. Actions: get_text (lit), set_text (écrit), get_history (dernières copiés), clear (vide l'historique).";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "get_text, set_text, get_history, clear", typeof(string), required: true),
        new ToolParameter("content", "Texte à copier (pour set_text)", typeof(string)),
    };

    public ClipboardTool(ILogger<ClipboardTool> logger)
    {
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("content", out var content);

        return (action?.ToLowerInvariant()) switch
        {
            "get_text" => GetTextAsync(),
            "set_text" => SetTextAsync(content),
            "get_history" => GetHistory(),
            "clear" => ClearHistory(),
            _ => Task.FromResult(ToolResult.Failed($"Action inconnue: {action}. Valides: get_text, set_text, get_history, clear"))
        };
    }

    private Task<ToolResult> GetTextAsync()
    {
        try
        {
            var text = GetClipboardText();
            if (text == null)
                return Task.FromResult(ToolResult.Succeeded("Le presse-papier est vide ou contient des données non-texte."));

            AddToHistory(text);
            _logger.LogInformation("[ClipboardTool] Got clipboard text ({Length} chars)", text.Length);
            return Task.FromResult(ToolResult.Succeeded(text));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClipboardTool] Failed to read clipboard");
            return Task.FromResult(ToolResult.Failed($"Erreur lecture presse-papier: {ex.Message}"));
        }
    }

    private Task<ToolResult> SetTextAsync(string? content)
    {
        if (content == null)
            return Task.FromResult(ToolResult.Failed("Paramètre 'content' requis pour set_text"));

        try
        {
            SetClipboardText(content);
            AddToHistory(content);
            _logger.LogInformation("[ClipboardTool] Set clipboard text ({Length} chars)", content.Length);
            return Task.FromResult(ToolResult.Succeeded("Presse-papier mis à jour."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClipboardTool] Failed to set clipboard");
            return Task.FromResult(ToolResult.Failed($"Erreur écriture presse-papier: {ex.Message}"));
        }
    }

    private static Task<ToolResult> GetHistory()
    {
        lock (_historyLock)
        {
            if (_history.Count == 0)
                return Task.FromResult(ToolResult.Succeeded("Aucun historique."));

            var sb = new StringBuilder();
            sb.AppendLine($"Historique presse-papier ({_history.Count} entrées) :");
            var recent = _history.TakeLast(10).ToList();
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var (text, time) = recent[i];
                var preview = text.Length > 80 ? text[..80] + "..." : text;
                preview = preview.Replace("\n", " ").Replace("\r", "");
                sb.AppendLine($"  [{time:HH:mm:ss}] {preview}");
            }
            return Task.FromResult(ToolResult.Succeeded(sb.ToString()));
        }
    }

    private static Task<ToolResult> ClearHistory()
    {
        lock (_historyLock)
        {
            _history.Clear();
        }
        return Task.FromResult(ToolResult.Succeeded("Historique effacé."));
    }

    private static void AddToHistory(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_historyLock)
        {
            _history.Add((text, DateTime.Now));
            if (_history.Count > 50)
                _history.RemoveRange(0, _history.Count - 50);
        }
    }

    private static string? GetClipboardText()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        if (!OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
                return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                var text = Marshal.PtrToStringUni(pointer);
                return text;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void SetClipboardText(string text)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Clipboard only supported on Windows");

        for (int retry = 0; retry < 3; retry++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var ptr = Marshal.StringToHGlobalUni(text);
                    SetClipboardData(CF_UNICODETEXT, ptr);
                    return;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(50);
        }

        throw new InvalidOperationException("Could not open clipboard after retries");
    }

    private const int CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
