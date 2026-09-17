using System.Text.Json;
using JarvisAI.Web.Configuration;

namespace JarvisAI.Tests;

/// <summary>
/// L'installateur ne dépose plus aucun appsettings.json : toute la configuration
/// par défaut vit dans <see cref="UserConfig.DefaultJson"/> et est écrite au
/// premier lancement dans %LOCALAPPDATA%\JarvisAI. Ces tests garantissent que
/// cette source unique reste valide et complète (une section oubliée = une
/// fonctionnalité silencieusement cassée après une installation neuve).
/// </summary>
public sealed class UserConfigTests
{
    [Fact]
    public void DefaultJson_is_valid_json()
    {
        using var doc = JsonDocument.Parse(UserConfig.DefaultJson);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void DefaultJson_covers_all_runtime_sections()
    {
        using var doc = JsonDocument.Parse(UserConfig.DefaultJson);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Logging", out _), "Logging manquant");
        Assert.True(root.TryGetProperty("AllowedHosts", out _), "AllowedHosts manquant");

        Assert.True(root.TryGetProperty("AI", out var ai), "AI manquant");
        foreach (var key in new[] { "DefaultProvider", "DefaultModel", "FastModel", "ReasoningModel", "OllamaBaseUrl" })
            Assert.True(ai.TryGetProperty(key, out _), $"AI:{key} manquant");

        Assert.True(root.TryGetProperty("JarvisAI", out var jarvis), "JarvisAI manquant");
        foreach (var section in new[] { "Ollama", "OpenAI", "Security", "Presence", "Budget", "Obs", "Hue", "Wol", "Personality" })
            Assert.True(jarvis.TryGetProperty(section, out _), $"JarvisAI:{section} manquant");
        Assert.True(jarvis.TryGetProperty("IPhoneToken", out _), "JarvisAI:IPhoneToken manquant");

        // Sans la section Update, plus aucune mise à jour automatique sur une install neuve.
        Assert.True(root.TryGetProperty("Update", out var update), "Update manquant");
        Assert.True(update.TryGetProperty("Owner", out _), "Update:Owner manquant");
        Assert.True(update.TryGetProperty("Repository", out _), "Update:Repository manquant");
    }

    [Fact]
    public void DefaultJson_ships_no_secrets()
    {
        using var doc = JsonDocument.Parse(UserConfig.DefaultJson);
        var jarvis = doc.RootElement.GetProperty("JarvisAI");

        Assert.Equal("", jarvis.GetProperty("OpenAI").GetProperty("ApiKey").GetString());
        Assert.Equal("", jarvis.GetProperty("Obs").GetProperty("Password").GetString());
        Assert.Equal("", jarvis.GetProperty("Hue").GetProperty("AppKey").GetString());
        Assert.Equal("", jarvis.GetProperty("IPhoneToken").GetString());
    }

    [Fact]
    public void ConfigFile_lives_in_localappdata_not_next_to_exe()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "appsettings.json");
        Assert.Equal(expected, UserConfig.FilePath);
    }
}
