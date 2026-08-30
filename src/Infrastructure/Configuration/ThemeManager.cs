using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Configuration;

public interface IThemeManager
{
    AppTheme CurrentTheme { get; }
    event EventHandler<AppTheme>? ThemeChanged;
    void SetTheme(AppTheme theme);
    void SetAccentColor(string colorHex);
    string GetAccentColor();
    AppThemeSettings GetSettings();
}

public sealed class ThemeManager : IThemeManager
{
    private readonly ILogger<ThemeManager> _logger;
    private readonly string _storagePath;
    private AppTheme _currentTheme;
    private string _accentColor = "#56CCF2";
    private AppThemeSettings _settings = new();

    public AppTheme CurrentTheme => _currentTheme;
    public event EventHandler<AppTheme>? ThemeChanged;

    public ThemeManager(ILogger<ThemeManager> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "theme.json");
        Load();
    }

    public void SetTheme(AppTheme theme)
    {
        _currentTheme = theme;
        Save();
        ThemeChanged?.Invoke(this, theme);
        _logger.LogInformation("[Theme] Changed to: {Theme}", theme);
    }

    public void SetAccentColor(string colorHex)
    {
        _accentColor = colorHex;
        Save();
    }

    public string GetAccentColor() => _accentColor;

    public AppThemeSettings GetSettings() => _settings;

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _settings = JsonSerializer.Deserialize<AppThemeSettings>(json) ?? new();
                _currentTheme = _settings.Theme;
                _accentColor = _settings.AccentColor;
            }
        }
        catch
        {
            _currentTheme = AppTheme.Dark;
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _settings.Theme = _currentTheme;
            _settings.AccentColor = _accentColor;
            _settings.LastModified = DateTime.UtcNow;

            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public enum AppTheme { Dark, Light, System }

public sealed class AppThemeSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string AccentColor { get; set; } = "#56CCF2";
    public bool HighContrast { get; set; }
    public double FontSize { get; set; } = 14;
    public DateTime LastModified { get; set; }
}
