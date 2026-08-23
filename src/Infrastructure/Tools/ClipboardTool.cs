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

    public string Name => "clipboard";
    public string Description => "Read from or write to the system clipboard. Actions: get_text, set_text";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Operation: get_text, set_text", typeof(string), required: true),
        new ToolParameter("content", "Text content to set on clipboard (for set_text)", typeof(string)),
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
            _ => Task.FromResult(ToolResult.Failed($"Unknown action: {action}. Valid: get_text, set_text"))
        };
    }

    private Task<ToolResult> GetTextAsync()
    {
        try
        {
            var text = GetClipboardText();
            if (text == null)
                return Task.FromResult(ToolResult.Succeeded("Clipboard is empty or contains non-text data"));

            _logger.LogInformation("[ClipboardTool] Got clipboard text ({Length} chars)", text.Length);
            return Task.FromResult(ToolResult.Succeeded(text));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClipboardTool] Failed to read clipboard");
            return Task.FromResult(ToolResult.Failed($"Failed to read clipboard: {ex.Message}"));
        }
    }

    private Task<ToolResult> SetTextAsync(string? content)
    {
        if (content == null)
            return Task.FromResult(ToolResult.Failed("Parameter 'content' is required for set_text"));

        try
        {
            SetClipboardText(content);
            _logger.LogInformation("[ClipboardTool] Set clipboard text ({Length} chars)", content.Length);
            return Task.FromResult(ToolResult.Succeeded("Clipboard updated successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClipboardTool] Failed to set clipboard");
            return Task.FromResult(ToolResult.Failed($"Failed to set clipboard: {ex.Message}"));
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
            throw new PlatformNotSupportedException("Clipboard is only supported on Windows");

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
