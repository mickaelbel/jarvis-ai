using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IEmbeddableWebViewService
{
    string CreateWebViewConfig(string name, string content, WebViewType type = WebViewType.Chat);
    WebViewConfig? GetConfig(string configId);
    IReadOnlyList<WebViewConfig> GetAllConfigs();
    void UpdateConfig(string configId, string content);
    void DeleteConfig(string configId);
    string GenerateEmbedCode(string configId, int width = 400, int height = 600);
}

public sealed class EmbeddableWebViewService : IEmbeddableWebViewService
{
    private readonly ILogger<EmbeddableWebViewService> _logger;
    private readonly string _storagePath;
    private readonly List<WebViewConfig> _configs = new();

    public EmbeddableWebViewService(ILogger<EmbeddableWebViewService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "webview_configs.json");
        Load();
    }

    public string CreateWebViewConfig(string name, string content, WebViewType type = WebViewType.Chat)
    {
        var config = new WebViewConfig
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Content = content,
            Type = type,
            CreatedAt = DateTime.UtcNow
        };

        _configs.Add(config);
        Save();
        _logger.LogInformation("[WebView] Config created: {Name} ({Type})", name, type);
        return config.Id;
    }

    public WebViewConfig? GetConfig(string configId)
        => _configs.FirstOrDefault(c => c.Id == configId);

    public IReadOnlyList<WebViewConfig> GetAllConfigs() => _configs.ToList();

    public void UpdateConfig(string configId, string content)
    {
        var config = _configs.FirstOrDefault(c => c.Id == configId);
        if (config is not null)
        {
            config.Content = content;
            config.LastModified = DateTime.UtcNow;
            Save();
        }
    }

    public void DeleteConfig(string configId)
    {
        _configs.RemoveAll(c => c.Id == configId);
        Save();
    }

    public string GenerateEmbedCode(string configId, int width = 400, int height = 600)
    {
        var config = GetConfig(configId);
        if (config is null) return "<!-- Config not found -->";

        return $@"<iframe
  src=""jarvis://embed/{configId}""
  width=""{width}""
  height=""{height}""
  frameborder=""0""
  allow=""microphone; camera""
  title=""Jarvis - {config.Name}"">
</iframe>";
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<WebViewConfig>>(json);
                if (loaded is not null) _configs.AddRange(loaded);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_configs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class WebViewConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public WebViewType Type { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModified { get; set; }
}

public enum WebViewType { Chat, ToolOutput, Custom }
